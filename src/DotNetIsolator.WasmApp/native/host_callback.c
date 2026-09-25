#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wasm/driver.h>
#include <mono/metadata/object.h>
#include <limits.h>
#include <stddef.h>

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("call_host")))
int dotnetisolator_call_host(void* invocation, int invocation_length, void** result, int* result_length);

// Primitive values cross in registers. Only the exceptional path maps guest memory in the host.
__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("call_host_scalar")))
int64_t dotnetisolator_call_host_scalar(int callback_id, int64_t bits, int kinds, int* error);

static int64_t call_host_scalar(int callback_id, int kinds, int* error, int64_t bits) {
	*error = 0;
	return dotnetisolator_call_host_scalar(callback_id, bits, kinds, error);
}

// The internal call uses the existing (i32, i32, i32) -> void interpreter wrapper.
// The native adapter passes scalar bits through Wasm registers, with only an error pointer.
typedef struct ScalarArguments {
    int64_t a0, a1, a2, a3, result;
    int error;
} ScalarArguments;
_Static_assert(offsetof(ScalarArguments, error) == 40, "Scalar callback layout must match the guest.");

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("call_host_scalars")))
int64_t dotnetisolator_call_host_scalars(int callback_id, int kinds, int64_t a0, int64_t a1, int64_t a2, int64_t a3, int* error);

static void call_host_scalars(int callback_id, int kinds, ScalarArguments* args) {
    args->error = 0;
    args->result = dotnetisolator_call_host_scalars(callback_id, kinds, args->a0, args->a1, args->a2, args->a3, &args->error);
}

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("call_host_raw")))
int dotnetisolator_call_host_raw(int callback_id, void* args, int count, void** result, int* result_length);

typedef struct RawCallbackArgument {
	void* data;
	int length; // -1 means null; zero means a non-null empty array
	uint32_t handle;
} RawCallbackArgument;
_Static_assert(sizeof(RawCallbackArgument) == 12, "Raw callback descriptor layout must match the host.");

// Pin all arguments until the synchronous host call completes. The host receives fresh copies,
// and no MessagePack envelope or staging byte array is needed. Small arities stay on the stack.
static int call_host_raw(int callback_id, MonoArray* args, void** result, int* result_length) {
	*result = NULL;
	*result_length = 0;
	uintptr_t count = mono_array_length(args);
	if (count > INT_MAX / sizeof(RawCallbackArgument)) return 0;
	RawCallbackArgument local_args[8] = {0};
	RawCallbackArgument* entries = count <= 8 ? local_args : calloc(count, sizeof(RawCallbackArgument));
	if (!entries) return 0;
	uint32_t args_handle = mono_gchandle_new((MonoObject*)args, 1);
	for (uintptr_t i = 0; i < count; i++) {
		MonoArray* arg = mono_array_get(args, MonoArray*, i);
		if (!arg) {
			entries[i].length = -1;
			continue;
		}
		entries[i].handle = mono_gchandle_new((MonoObject*)arg, 1);
		entries[i].length = (int)mono_array_length(arg);
		entries[i].data = entries[i].length ? mono_array_addr_with_size(arg, 1, 0) : NULL;
	}
	int success = dotnetisolator_call_host_raw(callback_id, entries, (int)count, result, result_length);
	for (uintptr_t i = 0; i < count; i++) {
		if (entries[i].handle) mono_gchandle_free(entries[i].handle);
	}
	mono_gchandle_free(args_handle);
	if (entries != local_args) free(entries);
	return success;
}

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("resolve_callback")))
int dotnetisolator_resolve_callback(void* name, int name_len);

void dotnetisolator_free_host_call_result(void* result) {
	free(result);
}

void dotnetisolator_add_host_callback_internal_calls() {
	mono_add_internal_call("DotNetIsolator.Guest.Interop::CallHostScalars", call_host_scalars);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::CallHostRaw", call_host_raw);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::CallHost", dotnetisolator_call_host);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::ResolveCallback", dotnetisolator_resolve_callback);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::CallHostScalar", call_host_scalar);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::FreeHostCallResult", dotnetisolator_free_host_call_result);
}
