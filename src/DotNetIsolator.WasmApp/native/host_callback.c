#include <stdio.h>
#include <stdlib.h>
#include <wasm/driver.h>

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("call_host")))
int dotnetisolator_call_host(void* invocation, int invocation_length, void** result, int* result_length);

void dotnetisolator_free_host_call_result(void* result) {
	free(result);
}

void dotnetisolator_add_host_callback_internal_calls() {
	mono_add_internal_call("DotNetIsolator.Guest.Interop::CallHost", dotnetisolator_call_host);
	mono_add_internal_call("DotNetIsolator.Guest.Interop::FreeHostCallResult", dotnetisolator_free_host_call_result);
}
