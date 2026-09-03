# IlMatto Antigravity SDK Bridge

This directory contains the Python sidecar used by `IlMatto.ManagerHost` for
new Antigravity Companion sessions and image-bearing turns.

Development setup:

```powershell
uv venv --python 3.11.15 .venv
uv pip install --python .\.venv\Scripts\python.exe -r requirements.txt
```

The bridge speaks one JSON object per line on stdin and stdout. It is started
and stopped by ManagerHost; it is not a public HTTP service.

For development, set `ILMATTO_ANTIGRAVITY_PYTHON` to the Python executable
that has the pinned SDK installed. For a release build, place a self-contained
Python runtime with the installed `google-antigravity==0.1.15` wheel under
`AntigravityBridge/python/`; ManagerHost will select that runtime automatically.

The bridge intentionally accepts only image attachments from the current
Manager protocol. It validates count, extension, file type and size before
calling `Image.from_file()`. SDK session state is stored under the `saveDir/<conversationId>`
directory supplied by ManagerHost.
