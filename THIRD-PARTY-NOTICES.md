# Third-party notices

This package embeds data generated from the following third-party project.

---

## microsoft/microsoft-ui-xaml

**Used for**: `src/XamlKeys.Core/WinUiFrameworkKeys.json` — the list of resource key names that the
WinUI framework itself provides. The file contains **key names only** (no code, no styles, no
values), extracted from the theme resource dictionaries at the commit recorded in the file's
`source` field.

Regenerate with `scripts/regenerate-framework-keys.mjs`; the procedure is documented at the top of
that script. **When the data is regenerated, update the commit below to match.**

- Source: https://github.com/microsoft/microsoft-ui-xaml
- Commit: `9402930972f25cfcad82abc9f82ecab29936f404`
- License: MIT
- Regenerate: `node scripts/regenerate-framework-keys.mjs winui <clone> <sha>`

```
The MIT License (MIT)

Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## dotnet/wpf

**Used for**: `src/XamlKeys.Core/WpfFluentKeys.json` — the list of resource key names that the
Fluent theme built into WPF (`PresentationFramework.Fluent`, .NET 9 and later) provides. The file
contains **key names only**, extracted from that theme's resource dictionaries at the commit
recorded in the file's `source` field.

Regenerate with `node scripts/regenerate-framework-keys.mjs fluent <clone> <sha>`.
**When the data is regenerated, update the commit below to match.**

- Source: https://github.com/dotnet/wpf
- Commit: `e7acf467c05e51901b17684b1f2c330e7dc5fc4d`
- License: MIT

The MIT License text above applies to this project as well (Copyright (c) .NET Foundation and
Contributors).
