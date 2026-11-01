# FormatFlow V1

Portable Windows file converter with local processing, batch conversion and 41 output formats.
Current source version: **2.4.0** (.NET Framework 4.8, Windows x64).

- [Application source](FormatFlowNative/)
- [Downloads](../../releases)
- [Advertisement manager](https://yosef-chadad.github.io/formatflow-control/)

The public advertisement manager requires an authorized GitHub credential to publish changes.
No write credential is included in the site, application or public configuration.
Personal administration instructions are distributed separately to the owner.

Binary resources are restored from the included icon encoding, original sound generator and
pinned FFmpeg distribution using `python build-assets.py`. Build the application with
`dotnet build FormatFlowNative/FormatFlow.csproj -c Release`.

Third-party notices and applicable licenses are in
[Resources/Notices.txt](FormatFlowNative/Resources/Notices.txt).
The application invokes FFmpeg as a separate process. Its corresponding-source and distribution
references are retained in those notices. Publication of source does not change the embedded
application terms or separate third-party licenses.

The UI has two advertisement positions: a compact banner above the greeting and a lower banner
after selected file cards. Public JSON is data; the client does not execute remote scripts.
