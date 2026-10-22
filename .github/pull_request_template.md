## What

<!-- One or two sentences: what changes and why. Link the roadmap package or issue if there is one. -->

## Checklist

- [ ] The title follows the commit convention (`feat(scope): ...`, `fix(scope): ...`, `chore: ...`), which decides the next version
- [ ] `dotnet atom Test` passes locally (or the relevant suite does)
- [ ] Docs under `docs/` are updated where behaviour, configuration or commands changed
- [ ] If `_atom/IBuild.cs` changed, `dotnet atom Gen` was run and the files under `.github/` are committed
