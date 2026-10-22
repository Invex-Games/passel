# Writing Docs

This site is built with [DocFX](https://dotnet.github.io/docfx/) from Markdown in the repository.

| Path | Contents |
|---|---|
| `docfx.json` | Site configuration |
| `index.md` | The landing page |
| `toc.yml` | The top navigation |
| `docs/` | Every other page; `docs/toc.yml` is the sidebar |
| `docfx-theme/` | CSS overrides on top of the `modern` template |

To preview your changes, run:

```
dotnet atom ServeDocs
```

It builds the site into `_site/` and serves it at `http://localhost:8080` until you press Ctrl+C. `dotnet atom BuildDocs` only builds it, and copies the result to `atom-publish/GeneratedDocs`. DocFX runs through `dotnet tool exec`, so there's nothing to install.

When you add a page, list it in `docs/toc.yml`. Link between pages with relative `.md` paths; DocFX rewrites them and warns about broken links.
