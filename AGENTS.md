# Repository Guidelines

## Project Structure & Module Organization

The solution is organized under `src/` as layered .NET projects. `LitematicaViewer.Core` parses `.litematic` files; `Assets` resolves resource packs and textures; `Meshing` converts documents into renderable geometry; and `Previewer` contains reusable Avalonia/OpenGL rendering components. `SamplePreviewer` and `Previewer.Sample` are desktop hosts, while `ShellPreview` and `Setup` implement the Windows Explorer preview handler and installer.

Keep dependencies one-way: hosts may reference Core, Assets, Meshing, and Previewer, but lower layers must not reference UI hosts. Design notes live in `docs/`, helper scripts in `tools/`, resource packs in `packs/`, and generated artifacts in `dist/`. Put all temporary screenshots, logs, fixtures, and diagnostics in the ignored `test_temp/` directory.

## Build, Test, and Development Commands

- `dotnet build` — build the full solution with the SDK pinned by `global.json`.
- `dotnet run --project src/LitematicaViewer.Core -- <file.litematic>` — run parser and invariant smoke checks, optionally against real files.
- `dotnet run --project src/LitematicaViewer.Assets -- packs/vanilla-1.20.1.jar` — validate resource-pack resolution.
- `dotnet run --project src/LitematicaViewer.Meshing -- packs/vanilla-1.20.1.jar <file.litematic>` — exercise mesh generation.
- `dotnet run --project src/LitematicaViewer.SamplePreviewer -- <file.litematic>` — launch the interactive previewer.
- `python tools/build_installer.py` — publish the NativeAOT shell handler and create `dist/LitematicaViewer.Setup.exe`.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, LF endings, final newline, four spaces for C#, and two spaces for project/XML/JSON/Avalonia files. Use file-scoped namespaces, nullable annotations, implicit usings, and primary constructors where they improve clarity. Public types and members use `PascalCase`; locals and parameters use `camelCase`; private fields use `_camelCase`. Warnings are errors, so keep `dotnet build` clean. Restrict `unsafe` code to necessary native or GL interop.

## Testing Guidelines

Tests are in each layer's `Tests/` folder and use executable smoke harnesses with `Debug.Assert`, not xUnit. Add focused checks near the owning module, name fixtures by behavior, and verify both synthetic edge cases and representative real `.litematic` or resource-pack inputs. A passing harness must exit with code 0.

## Commit & Pull Request Guidelines

Follow the existing Conventional Commit style: `fix(assets): ...`, `feat(shell): ...`, `refactor(shell): ...`, or `docs: ...`. Keep commits scoped and imperative. Pull requests should explain the affected layer, list validation commands, link relevant issues, and include screenshots for rendering or UI changes. Call out installer, COM registration, NativeAOT, or resource-pack compatibility impacts explicitly.
