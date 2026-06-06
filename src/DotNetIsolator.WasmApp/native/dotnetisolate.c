#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/class.h>
#include <mono/metadata/metadata.h>
#include <mono/metadata/object.h>
#include <wasm/driver.h>

typedef struct RunnerInvocation {
	MonoGCHandle target;
	MonoMethod* method_ptr;
	MonoString* result_exception;
	void* result_serialized;
	int result_serialized_length;
	MonoGCHandle result_serialized_handle;
	void** args_length_prefixed_buffers;
	int args_length_prefixed_buffers_length;
} RunnerInvocation;

typedef struct ByteArrayInvocationResult {
	void* data;
	int length;
	MonoGCHandle result_handle;
	MonoString* error_msg;
} ByteArrayInvocationResult;

typedef struct BlittableArrayInvocationResult {
	void* data;
	int length; // element count
	int element_size; // bytes per element
	MonoGCHandle result_handle;
	MonoString* error_msg;
} BlittableArrayInvocationResult;

typedef struct BlittableArgInvocation {
	MonoGCHandle target;
	MonoMethod* method_ptr;
	void* arg_data; // raw element bytes (host-owned)
	int arg_length; // element count
	int arg_element_size; // bytes per element
	int arg_element_kind; // element-kind tag
	MonoString* result_exception;
	void* result_serialized;
	int result_serialized_length;
	MonoGCHandle result_serialized_handle;
} BlittableArgInvocation;

__attribute__((export_name("dotnetisolator_instantiate_class")))
MonoGCHandle dotnetisolator_instantiate_class(char* assembly_name, char* namespace, char* class_name, char** error_msg) {
	MonoGCHandle result;

	MonoAssembly* assembly = mono_wasm_assembly_load(assembly_name);
	if (!assembly) {
		asprintf(error_msg, "Could not load assembly '%s'", assembly_name);
	} else {
		MonoClass* class = mono_wasm_assembly_find_class(assembly, namespace, class_name);
		if (!class) {
			asprintf(error_msg, "Could not find type '%s.%s' in assembly '%s'", namespace, class_name, assembly_name);
		} else {
			MonoObject* instance = mono_object_new(NULL, class);
			result = (MonoGCHandle)mono_gchandle_new(instance, /* pinned */ 0);
			mono_runtime_object_init(instance);
			*error_msg = NULL;
		}
	}

	free(assembly_name);
	free(namespace);
	free(class_name);
	return result;
}

__attribute__((export_name("dotnetisolator_release_object")))
void dotnetisolator_release_object(MonoGCHandle gcHandle) {
	if (gcHandle) {
		mono_gchandle_free((uint32_t)gcHandle);
	}
}

__attribute__((export_name("dotnetisolator_lookup_method")))
MonoMethod* dotnetisolator_lookup_method(char* assembly_name, char* namespace, char* type_name, char* method_name, int num_params, char** error_msg) {
	//printf("Trying to find %s %s %s %s %i\n", assembly_name, namespace, type_name, method_name, num_params);
	MonoMethod* result = NULL;
	*error_msg = NULL;
	char* namespace_or_fallback = namespace ? namespace : "(no namespace)";

	MonoAssembly* assembly = mono_wasm_assembly_load(assembly_name);
	if (!assembly) {
		asprintf(error_msg, "Could not find method [%s]%s.%s::%s because the assembly %s could not be found.", assembly_name, namespace_or_fallback, type_name, method_name, assembly_name);
	} else {
		MonoClass* class = mono_wasm_assembly_find_class(assembly, namespace, type_name);
		if (!class) {
			asprintf(error_msg, "Could not find method [%s]%s.%s::%s because the type %s could not be found in the assembly.", assembly_name, namespace_or_fallback, type_name, method_name, type_name);
		} else {
			result = mono_wasm_assembly_find_method(class, method_name, num_params);
			if (!result) {
				asprintf(error_msg, "Could not find method [%s]%s.%s::%s because the method %s could not be found in the type, or it did not have the correct number of parameters.", assembly_name, namespace_or_fallback, type_name, method_name, method_name);
			}
		}
	}

	free(assembly_name);
	free(namespace);
	free(type_name);
	free(method_name);
	return result;
}

MonoMethod* deserialize_param_dotnet_method;
MonoMethod* serialize_return_value_dotnet_method;

int fail_with_message(const char* message, MonoString** error_msg) {
	*error_msg = mono_string_new_wrapper(message);
	return 0;
}

int method_signature_is_i32_i32(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	if (mono_signature_get_param_count(signature) != 1) {
		return 0;
	}

	void* iterator = NULL;
	MonoType* parameter_type = mono_signature_get_params(signature, &iterator);
	MonoType* return_type = mono_signature_get_return_type(signature);

	return mono_type_get_type(parameter_type) == MONO_TYPE_I4
		&& mono_type_get_type(return_type) == MONO_TYPE_I4;
}

int method_signature_is_i32(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	MonoType* return_type = mono_signature_get_return_type(signature);

	return mono_signature_get_param_count(signature) == 0
		&& mono_type_get_type(return_type) == MONO_TYPE_I4;
}

int method_signature_is_void(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	MonoType* return_type = mono_signature_get_return_type(signature);

	return mono_signature_get_param_count(signature) == 0
		&& mono_type_get_type(return_type) == MONO_TYPE_VOID;
}

int method_signature_is_i32_void(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	if (mono_signature_get_param_count(signature) != 1) {
		return 0;
	}

	void* iterator = NULL;
	MonoType* parameter_type = mono_signature_get_params(signature, &iterator);
	MonoType* return_type = mono_signature_get_return_type(signature);

	return mono_type_get_type(parameter_type) == MONO_TYPE_I4
		&& mono_type_get_type(return_type) == MONO_TYPE_VOID;
}

// Returns the element size in bytes for a blittable primitive element class, or 0 if the
// element type is not a blittable primitive that can be transferred as a raw memory block.
int blittable_element_size(MonoClass* element_class) {
	if (element_class == mono_get_byte_class()
		|| element_class == mono_get_sbyte_class()
		|| element_class == mono_get_boolean_class()) {
		return 1;
	}

	if (element_class == mono_get_int16_class()
		|| element_class == mono_get_uint16_class()
		|| element_class == mono_get_char_class()) {
		return 2;
	}

	if (element_class == mono_get_int32_class()
		|| element_class == mono_get_uint32_class()
		|| element_class == mono_get_single_class()) {
		return 4;
	}

	if (element_class == mono_get_int64_class()
		|| element_class == mono_get_uint64_class()
		|| element_class == mono_get_double_class()) {
		return 8;
	}

	return 0;
}

// Maps an element-kind tag (shared with the managed ObjectGraphPrimitiveCollections codec) to its
// MonoClass, or NULL for an unknown tag.
MonoClass* element_class_for_kind(int kind) {
	switch (kind) {
		case 1: return mono_get_boolean_class();
		case 2: return mono_get_sbyte_class();
		case 3: return mono_get_byte_class();
		case 4: return mono_get_int16_class();
		case 5: return mono_get_uint16_class();
		case 6: return mono_get_char_class();
		case 7: return mono_get_int32_class();
		case 8: return mono_get_uint32_class();
		case 9: return mono_get_int64_class();
		case 10: return mono_get_uint64_class();
		case 11: return mono_get_single_class();
		case 12: return mono_get_double_class();
		default: return NULL;
	}
}

// Returns the element size for a () -> blittable[] method, or 0 if the signature does not match.
int blittable_array_return_element_size(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	if (mono_signature_get_param_count(signature) != 0) {
		return 0;
	}

	MonoType* return_type = mono_signature_get_return_type(signature);
	if (mono_type_get_type(return_type) != MONO_TYPE_SZARRAY) {
		return 0;
	}

	MonoClass* array_class = mono_class_from_mono_type(return_type);
	MonoClass* element_class = mono_class_get_element_class(array_class);
	return blittable_element_size(element_class);
}

int method_signature_is_byte_array(MonoMethod* method) {
	MonoMethodSignature* signature = mono_method_signature(method);
	if (mono_signature_get_param_count(signature) != 0) {
		return 0;
	}

	MonoType* return_type = mono_signature_get_return_type(signature);
	if (mono_type_get_type(return_type) != MONO_TYPE_SZARRAY) {
		return 0;
	}

	MonoClass* array_class = mono_class_from_mono_type(return_type);
	MonoClass* element_class = mono_class_get_element_class(array_class);
	return element_class == mono_get_byte_class();
}

void* deserialize_param(void* length_prefixed_buffer, MonoGCHandle* value_handle, MonoObject** exception_buf) {
	if (!length_prefixed_buffer) {
		return NULL;
	}

	if (deserialize_param_dotnet_method == 0) {
		deserialize_param_dotnet_method = lookup_dotnet_method("DotNetIsolator.WasmApp", "DotNetIsolator.WasmApp", "Serialization", "Deserialize", -1);
	}

	void* method_params[] = { length_prefixed_buffer + 4, length_prefixed_buffer };
	MonoObject* result = mono_runtime_invoke(
		deserialize_param_dotnet_method,
		NULL,
		method_params,
		exception_buf);

	if (!result) {
		*value_handle = NULL;
		return NULL;
	}

	// I don't actually know for sure if it's necessary to pin these MonoObject* for the duration between
	// deserializing and calling the method. But given that we're unboxing value types and getting back a
	// raw pointer to the value-type memory, we do need it not to move in this period.
	*value_handle = (MonoGCHandle)mono_gchandle_new(result, /* pinned */ 1);

	int must_unbox = mono_class_is_valuetype(mono_object_get_class(result));
	return must_unbox ? mono_object_unbox(result) : result;
}

void serialize_value_into(MonoObject* value, void** out_data, int* out_length, MonoGCHandle* out_handle, MonoObject** exception_buf) {
	if (!value) {
		*out_data = NULL;
		return;
	}

	if (serialize_return_value_dotnet_method == 0) {
		serialize_return_value_dotnet_method = lookup_dotnet_method("DotNetIsolator.WasmApp", "DotNetIsolator.WasmApp", "Serialization", "Serialize", -1);
	}

	void* method_params[] = { value };
	MonoObject* byte_array = mono_runtime_invoke(serialize_return_value_dotnet_method, NULL, method_params, exception_buf);
	*out_data = mono_array_addr_with_size((MonoArray*)byte_array, 1, 0);
	*out_length = mono_array_length((MonoArray*)byte_array);
	*out_handle = (MonoGCHandle)mono_gchandle_new(byte_array, /* pinned */ 1);
}

void serialize_return_value(MonoObject* value, RunnerInvocation* invocation, MonoObject** exception_buf) {
	serialize_value_into(value, &invocation->result_serialized, &invocation->result_serialized_length, &invocation->result_serialized_handle, exception_buf);
}

__attribute__((export_name("dotnetisolator_invoke_method")))
void dotnetisolator_invoke_method(RunnerInvocation* invocation) {
	MonoObject* exc = NULL;

	int num_args = invocation->args_length_prefixed_buffers_length;
	void* method_params[num_args];
	MonoGCHandle arg_handles[num_args];
	for (int i = 0; i < num_args; i++) {
		void* arg_length_prefixed_buffer = invocation->args_length_prefixed_buffers[i];
		method_params[i] = deserialize_param(arg_length_prefixed_buffer, &arg_handles[i], &exc);
		if (exc) {
			break;
		}
	}

	free(invocation->args_length_prefixed_buffers);

	if (!exc) {
		//printf("GCHandle: %p, Method: %p, Arg0: %p\n", invocation->target, invocation->method_ptr, invocation->arg0);
		MonoObject* target = invocation->target ? mono_gchandle_get_target((uint32_t)(invocation->target)) : 0;

		MonoObject* result = mono_runtime_invoke(invocation->method_ptr, target, method_params, &exc);

		for (int i = 0; i < num_args; i++) {
			if (arg_handles[i]) {
				mono_gchandle_free((uint32_t)arg_handles[i]);
			}
		}

		if (!exc) {
			serialize_return_value(result, invocation, &exc);
		}
	}

	if (exc) {
		// Return a string representation of the error
		MonoObject* ignored_tostring_exception;
		invocation->result_exception = mono_object_to_string(exc, &ignored_tostring_exception);
	}
}

static int invoke_i32_i32(MonoGCHandle target, MonoMethod* method_ptr, int arg0, int* result, MonoString** error_msg) {
	*error_msg = NULL;

	if (!method_signature_is_i32_i32(method_ptr)) {
		return fail_with_message("The method does not have the required int -> int signature.", error_msg);
	}

	void* method_params[] = { &arg0 };
	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	MonoObject* result_object = mono_runtime_invoke(method_ptr, target_object, method_params, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		*error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return 0;
	}

	if (!result_object) {
		return fail_with_message("The method returned null instead of int.", error_msg);
	}

	*result = *(int*)mono_object_unbox(result_object);
	return 1;
}

static int invoke_i32(MonoGCHandle target, MonoMethod* method_ptr, int* result, MonoString** error_msg) {
	*error_msg = NULL;

	if (!method_signature_is_i32(method_ptr)) {
		return fail_with_message("The method does not have the required () -> int signature.", error_msg);
	}

	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	MonoObject* result_object = mono_runtime_invoke(method_ptr, target_object, NULL, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		*error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return 0;
	}

	if (!result_object) {
		return fail_with_message("The method returned null instead of int.", error_msg);
	}

	*result = *(int*)mono_object_unbox(result_object);
	return 1;
}

static int invoke_void(MonoGCHandle target, MonoMethod* method_ptr, MonoString** error_msg) {
	*error_msg = NULL;

	if (!method_signature_is_void(method_ptr)) {
		return fail_with_message("The method does not have the required () -> void signature.", error_msg);
	}

	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	mono_runtime_invoke(method_ptr, target_object, NULL, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		*error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return 0;
	}

	return 1;
}

static int invoke_i32_void(MonoGCHandle target, MonoMethod* method_ptr, int arg0, MonoString** error_msg) {
	*error_msg = NULL;

	if (!method_signature_is_i32_void(method_ptr)) {
		return fail_with_message("The method does not have the required int -> void signature.", error_msg);
	}

	void* method_params[] = { &arg0 };
	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	mono_runtime_invoke(method_ptr, target_object, method_params, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		*error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return 0;
	}

	return 1;
}

static void invoke_byte_array(MonoGCHandle target, MonoMethod* method_ptr, ByteArrayInvocationResult* result) {
	result->data = NULL;
	result->length = 0;
	result->result_handle = NULL;
	result->error_msg = NULL;

	if (!method_signature_is_byte_array(method_ptr)) {
		fail_with_message("The method does not have the required () -> byte[] signature.", &result->error_msg);
		return;
	}

	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	MonoObject* result_object = mono_runtime_invoke(method_ptr, target_object, NULL, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		result->error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return;
	}

	if (!result_object) {
		return;
	}

	MonoArray* result_array = (MonoArray*)result_object;
	uintptr_t result_length = mono_array_length(result_array);
	if (result_length > INT32_MAX) {
		fail_with_message("The byte array result is too large.", &result->error_msg);
		return;
	}

	result->result_handle = (MonoGCHandle)mono_gchandle_new(result_object, /* pinned */ 1);
	result->length = (int)result_length;
	result->data = result->length == 0
		? NULL
		: mono_array_addr_with_size(result_array, 1, 0);
}

static void invoke_blittable_array(MonoGCHandle target, MonoMethod* method_ptr, BlittableArrayInvocationResult* result) {
	result->data = NULL;
	result->length = 0;
	result->element_size = 0;
	result->result_handle = NULL;
	result->error_msg = NULL;

	int element_size = blittable_array_return_element_size(method_ptr);
	if (element_size == 0) {
		fail_with_message("The method does not have the required () -> blittable primitive array signature.", &result->error_msg);
		return;
	}

	MonoObject* exc = NULL;
	MonoObject* target_object = target ? mono_gchandle_get_target((uint32_t)target) : 0;
	MonoObject* result_object = mono_runtime_invoke(method_ptr, target_object, NULL, &exc);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		result->error_msg = mono_object_to_string(exc, &ignored_tostring_exception);
		return;
	}

	if (!result_object) {
		return;
	}

	MonoArray* result_array = (MonoArray*)result_object;
	uintptr_t result_length = mono_array_length(result_array);
	if (result_length > INT32_MAX) {
		fail_with_message("The array result is too large.", &result->error_msg);
		return;
	}

	result->result_handle = (MonoGCHandle)mono_gchandle_new(result_object, /* pinned */ 1);
	result->length = (int)result_length;
	result->element_size = element_size;
	result->data = result->length == 0
		? NULL
		: mono_array_addr_with_size(result_array, element_size, 0);
}

__attribute__((export_name("dotnetisolator_invoke_i32_i32")))
int dotnetisolator_invoke_i32_i32(MonoGCHandle target, MonoMethod* method_ptr, int arg0, int* result, MonoString** error_msg) {
	return invoke_i32_i32(target, method_ptr, arg0, result, error_msg);
}

__attribute__((export_name("dotnetisolator_invoke_byte_array")))
void dotnetisolator_invoke_byte_array(ByteArrayInvocationResult* result, MonoGCHandle target, MonoMethod* method_ptr) {
	invoke_byte_array(target, method_ptr, result);
}

__attribute__((export_name("dotnetisolator_invoke_blittable_array")))
void dotnetisolator_invoke_blittable_array(BlittableArrayInvocationResult* result, MonoGCHandle target, MonoMethod* method_ptr) {
	invoke_blittable_array(target, method_ptr, result);
}

// Zero-copy fast path for exact (T[]) -> TRes methods where T is a blittable primitive. The host
// supplies the raw element bytes; the guest materializes a managed array directly via mono_array_new
// plus a single memcpy, invokes the method, and serializes the result through the normal path.
__attribute__((export_name("dotnetisolator_invoke_blittable_array_arg")))
void dotnetisolator_invoke_blittable_array_arg(BlittableArgInvocation* invocation) {
	invocation->result_exception = NULL;
	invocation->result_serialized = NULL;
	invocation->result_serialized_length = 0;
	invocation->result_serialized_handle = NULL;

	MonoClass* element_class = element_class_for_kind(invocation->arg_element_kind);
	if (!element_class) {
		invocation->result_exception = mono_string_new_wrapper("Unknown blittable element kind in argument.");
		return;
	}

	MonoArray* array = mono_array_new(mono_domain_get(), element_class, (uintptr_t)invocation->arg_length);
	if (invocation->arg_length > 0) {
		void* dest = mono_array_addr_with_size(array, invocation->arg_element_size, 0);
		memcpy(dest, invocation->arg_data, (size_t)invocation->arg_length * (size_t)invocation->arg_element_size);
	}

	// Pin the array so it does not move while the invoked method runs.
	MonoGCHandle array_handle = (MonoGCHandle)mono_gchandle_new((MonoObject*)array, /* pinned */ 1);

	MonoObject* exc = NULL;
	MonoObject* target = invocation->target ? mono_gchandle_get_target((uint32_t)invocation->target) : 0;
	void* method_params[] = { array };
	MonoObject* result = mono_runtime_invoke(invocation->method_ptr, target, method_params, &exc);

	if (!exc) {
		serialize_value_into(result, &invocation->result_serialized, &invocation->result_serialized_length, &invocation->result_serialized_handle, &exc);
	}

	mono_gchandle_free((uint32_t)array_handle);

	if (exc) {
		MonoObject* ignored_tostring_exception;
		invocation->result_exception = mono_object_to_string(exc, &ignored_tostring_exception);
	}
}

__attribute__((export_name("dotnetisolator_invoke_i32_packed")))
uint64_t dotnetisolator_invoke_i32_packed(MonoGCHandle target, MonoMethod* method_ptr) {
	int result = 0;
	MonoString* error_msg = NULL;
	if (!invoke_i32(target, method_ptr, &result, &error_msg)) {
		uint32_t error_address = error_msg ? (uint32_t)(uintptr_t)error_msg : 1;
		return ((uint64_t)error_address) << 32;
	}

	return (uint32_t)result;
}

__attribute__((export_name("dotnetisolator_invoke_i32_i32_packed")))
uint64_t dotnetisolator_invoke_i32_i32_packed(MonoGCHandle target, MonoMethod* method_ptr, int arg0) {
	int result = 0;
	MonoString* error_msg = NULL;
	if (!invoke_i32_i32(target, method_ptr, arg0, &result, &error_msg)) {
		uint32_t error_address = error_msg ? (uint32_t)(uintptr_t)error_msg : 1;
		return ((uint64_t)error_address) << 32;
	}

	return (uint32_t)result;
}

__attribute__((export_name("dotnetisolator_invoke_void")))
uint32_t dotnetisolator_invoke_void(MonoGCHandle target, MonoMethod* method_ptr) {
	MonoString* error_msg = NULL;
	if (!invoke_void(target, method_ptr, &error_msg)) {
		return error_msg ? (uint32_t)(uintptr_t)error_msg : 1;
	}

	return 0;
}

__attribute__((export_name("dotnetisolator_invoke_i32_void")))
uint32_t dotnetisolator_invoke_i32_void(MonoGCHandle target, MonoMethod* method_ptr, int arg0) {
	MonoString* error_msg = NULL;
	if (!invoke_i32_void(target, method_ptr, arg0, &error_msg)) {
		return error_msg ? (uint32_t)(uintptr_t)error_msg : 1;
	}

	return 0;
}

__attribute__((export_name("dotnetisolator_deserialize_object")))
MonoGCHandle dotnetisolator_deserialize_object(void* length_prefixed_buffer, MonoString** error_monostring) {
	//printf("addr: %p; len: %i; first: %i\n", length_prefixed_buffer, ((int*)length_prefixed_buffer)[0], ((unsigned char*)length_prefixed_buffer)[4]);
	MonoGCHandle result;
	MonoObject* deserialization_exception = NULL;
	deserialize_param(length_prefixed_buffer, &result, &deserialization_exception);

	if (deserialization_exception) {
		// Return a string representation of the error
		MonoObject* ignored_tostring_exception;
		*error_monostring = mono_object_to_string(deserialization_exception, &ignored_tostring_exception);
		return NULL;
	} else {
		*error_monostring = NULL;
		return result;
	}
}
