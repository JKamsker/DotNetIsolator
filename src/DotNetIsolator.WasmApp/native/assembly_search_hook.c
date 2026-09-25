#include <wasm/driver.h>
#include <mono/metadata/class.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/image.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

__attribute__((import_module("dotnetisolator")))
__attribute__((import_name("request_assembly")))
int request_assembly(const char* assembly_name, int assembly_name_len, void** supplied_bytes, int* supplied_bytes_len);

// mono_assembly_load_from also calls the search hooks, so we'll get into an infinite recursive loop
// unless we explicitly stop the search hook from running inside itself
int assembly_search_hook_in_progress = 0;

MonoAssembly* dotnetisolator_assembly_search_hook(MonoAssemblyName* aname, void* user_data) {
	MonoAssembly* result = NULL;

	if (!assembly_search_hook_in_progress && !getenv("DISABLE_ASSEMBLY_SEARCH_HOOK")) {
        // This API invokes the search hooks too. Suppress ours while Mono's remaining hooks
        // check the default load context, which is also where we load supplied images below.
        assembly_search_hook_in_progress = 1;
        result = mono_assembly_loaded(aname);
        assembly_search_hook_in_progress = 0;
        if (result) return result;

		const char* assembly_name = mono_assembly_name_get_name(aname);
		void* loaded_bytes;
		int loaded_bytes_len;
		int success = request_assembly(assembly_name, strlen(assembly_name), &loaded_bytes, &loaded_bytes_len);
		if (success) {
			MonoImageOpenStatus status;
			MonoImage* image = mono_image_open_from_data(loaded_bytes, loaded_bytes_len, 1, &status);
            free(loaded_bytes); // need_copy=1 gives the image its own storage.
            if (image) {
                assembly_search_hook_in_progress = 1;
                result = mono_assembly_load_from(image, assembly_name, &status);
                assembly_search_hook_in_progress = 0;
                mono_image_close(image); // A successfully loaded assembly holds its own image reference.
            }
		}
	}

	return result;
}

void dotnetisolator_add_assembly_search_hook() {
	mono_install_assembly_search_hook(dotnetisolator_assembly_search_hook, NULL);
}
