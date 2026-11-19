# CodeQL alert triage

This desktop port treats a successful CodeQL workflow and an empty alert list as separate checks.
The seven alerts present at the start of the review were inspected against the exact source and data
flow on 2026-08-22. Alerts are not dismissed merely to make the list green. The first post-merge
analysis reports five results: #1 and #5 closed automatically after the unused Uno WASM sample was
removed from the desktop application's analysis scope; the five documented code paths remain open.

| Alert | Query | Assessment | Action |
| --- | --- | --- | --- |
| #1 | Missing X-Frame-Options | Out of scope. The file belongs to the unused upstream `UnoUI.Wasm` sample and is not referenced, built, served or packaged by this desktop app. | Resolved by excluding only that sample path in the CodeQL configuration. |
| #2 | Exposure of sensitive information | The port uses netDxf only to read overlays; no Avalonia path calls `DxfDocument.Save`. CodeQL follows a possible value into the bundled library's generic text writer without an application call site. | Retain the reader dependency and re-check if DXF export is added later. |
| #3, #4 | Exposure of sensitive information | Expected parameter-file serialization. The exporter writes the numeric values explicitly requested by the operator. | Add a reject-by-default sensitive-data warning before Raw Parameters and DroneCAN parameter exports. |
| #5 | Missing global error handler | Out of scope for the same unused `UnoUI.Wasm` sample as #1. | Resolved by excluding only that sample path in the CodeQL configuration. |
| #6 | ECB encryption | False positive for the WinZip AES construction. `ZipAESTransform` encrypts an incrementing nonce one block at a time to implement CTR, then authenticates ciphertext with HMAC. ECB is the required block primitive here, not the data mode. | Retain the interoperable WinZip AES implementation; do not replace it with plain ECB encryption or silently change the ZIP format. |
| #7 | Exposure of sensitive information | Expected decoded tlog export. GPS fields are intentionally retained in CSV/text output selected by the operator. | Add a reject-by-default warning covering precise coordinates, identities, missions, network details and parameters before every tlog-derived export. |

The privacy alerts remain useful documentation of sensitive export boundaries even when the flow is
intentional. Any dismissal should preserve these reasons in the GitHub alert history and should be
performed only as a separate, explicitly authorized review action.
