/*
 * Native C ABI smoke for the reviewed, compiler-free Wasmtime C API build.
 *
 * This uses ONLY public Wasmtime headers and empty Stores. It intentionally
 * does not deserialize arbitrary .cwasm bytes (which would be unsafe), import
 * host functions, compile Wasm or execute tenant code.
 *
 * A successful smoke proves native C linkage and a small lifecycle slice,
 * NOT .NET binding compatibility, guest AOT, memory isolation or release safety.
 */
#include <stddef.h>
#include <stdio.h>
#include <wasm.h>
#include <wasmtime.h>

int main(void) {
    wasm_engine_t *engine = wasm_engine_new();
    if (engine == NULL) {
        fputs("WASMTIME_C_API_ENGINE_FAILED\n", stderr);
        return 1;
    }

    wasmtime_store_t *a = wasmtime_store_new(engine, NULL, NULL);
    wasmtime_store_t *b = wasmtime_store_new(engine, NULL, NULL);
    if (a == NULL || b == NULL) {
        fputs("WASMTIME_C_API_STORE_FAILED\n", stderr);
        if (a != NULL) wasmtime_store_delete(a);
        if (b != NULL) wasmtime_store_delete(b);
        wasm_engine_delete(engine);
        return 1;
    }

    /* Independent Stores must be creatable and physically destroyed. No
     * assumption is made that this alone proves tenant memory isolation. */
    wasmtime_store_delete(a);
    wasmtime_store_delete(b);
    wasm_engine_delete(engine);

    puts("WASMTIME_COMPILER_FREE_C_ABI_STORE_PASS");
    return 0;
}
