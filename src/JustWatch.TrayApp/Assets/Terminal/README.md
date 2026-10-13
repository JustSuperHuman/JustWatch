# Bundled terminal assets

These are the same releases previously loaded from unpkg at runtime:

- `xterm` 5.3.0: `css/xterm.css`, `lib/xterm.js`
- `xterm-addon-fit` 0.8.0: `lib/xterm-addon-fit.js`
- `xterm-addon-web-links` 0.9.0: `lib/xterm-addon-web-links.js`

Files are unmodified from the official npm registry tarballs. Each package's
MIT license is included, and `manifest.json` records versions and SHA-256 hashes.
The app embeds the assets and licenses in its assembly. `TerminalHtml` inlines
the CSS and JavaScript so opening the Windows UI works without internet access.
No package restore or download is required for these assets during build/startup.

When updating, obtain each package's exact version from
`https://registry.npmjs.org/<package>/<version>`, verify the tarball against its
`dist` integrity metadata, and copy the files listed above plus `LICENSE`.
Update the manifest and run:

```powershell
dotnet run --project src/JustWatch.Test -- --windows-ui-tests
bun run update
```
