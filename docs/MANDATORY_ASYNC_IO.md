# Mandatory nonblocking guest I/O

Core I/O is always `native-async-only`. There is no policy switch, environment
compatibility setting or blocking worker adapter. This bootstrap backend has
no guest fs/HTTP/network/stdio bindings or continuation lowering; such source
rejects before CIL emission. Unknown `_async` names do not create support.

`RuntimeHost.Evaluate` returns an ExecutionResult (status, optional scalar,
typed arithmetic error) without writing to Console. The legacy Execute ABI
returns status without output. A throwing console writer regression exercises
success and arithmetic failures. The trusted NativeHost command-line wrapper
formats results outside guest execution; compiler source/artifact I/O and host
CLI diagnostics are not guest bindings.

`verification/mandatory-async-io.json` is the shared 22-case corpus. The test
suite checks both arithmetic and structured frontends. The CLI regression
checks check/compile, no partial artifacts, preservation of previous outputs,
and rejection of attempted compatibility flags:

```
python3 verification/assert-mandatory-async-cli.py /absolute/path/to/dotnet /absolute/path/to/oresnet.dll
```

The runtime still has no async guest I/O implementation. These rejection tests
prove denial of the fixture cases, not full effect checking or async support.
Native AOT and Windows execution require their independent existing gates.
The local macOS Native AOT probe correctly refused its unsupported RID; no
platform gate was bypassed. Linux/Windows execution is required separately.
