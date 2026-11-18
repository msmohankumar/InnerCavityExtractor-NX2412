# Inner Cavity Extractor – Siemens NX 2412 (C# NXOpen DLL)

This is a first-pass C# NXOpen Class Library conversion of the uploaded Python journal. It follows the original workflow: select outer body, extract a duplicate, select cavity seed faces, delete faces using the Boss/Pocket rule, subtract the original body, unite results if needed, isolate the cavity, calculate measurements, capture a snapshot, and open the HTA report.

## Important validation note

This project has been prepared against the NXOpen .NET API patterns and references, but it has **not been compiled or executed against your local NX 2412 installation** because the NX 2412 managed assemblies and a test CAD part are not available in this environment. NXOpen signatures can differ by release/maintenance pack. Treat this as the conversion source to build and validate, not as a certified production DLL. In particular, confirm the `UFDisp.CreateImage` overload, `Edge.GetVertices`, the `MeasureBodies` return type, and builder properties against the API installed with your NX 2412.

The source Python uses `AskBoundingBoxExact` / `AskBoundingBox` before an edge-vertex fallback. This C# version currently uses the edge-vertex fallback for the envelope dimensions. The fluid mass calculation intentionally preserves the source journal's assumed density of 1.0 g/cm³; it is not a material mass calculation.

## Requirements

- Siemens NX 2412 installed.
- Visual Studio with the .NET Framework 4.8 developer pack / targeting pack (or change the target to the framework specified by your NX 2412 installation).
- NX environment variable `UGII_BASE_DIR` set by the Siemens NX environment.
- NXOpen managed assemblies under `%UGII_BASE_DIR%\NXBIN\managed`.

## Build

1. Start Visual Studio.
2. Open `InnerCavityExtractor.csproj`.
3. If the NX references show unresolved, browse to `%UGII_BASE_DIR%\NXBIN\managed` and add `NXOpen.dll`, `NXOpen.UF.dll`, and `NXOpenUI.dll`.
4. Confirm the target framework matches the .NET runtime supported by your NX 2412 installation.
5. Build > Build Solution.
6. The output should be `bin\Debug\InnerCavityExtractor.dll` (or `bin\Release\...`).

## Run inside NX

1. Open a test part containing a closed solid with an internal cavity.
2. In NX, use **File > Execute > NX Open** (menu wording may vary by role/customization).
3. Browse to `InnerCavityExtractor.dll` and execute it.
4. Select the outer solid body, then select the internal cavity seed face(s).
5. Review the resulting isolated body and the report.

## Safety / test recommendation

Run first on a copy of a part. The automation creates and modifies NX features and blanks the original body in the current work part. Check the resulting Boolean feature and measurements before using it on production CAD data.
