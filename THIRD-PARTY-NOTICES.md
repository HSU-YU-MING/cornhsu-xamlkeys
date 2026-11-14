# Third-party notices

This package embeds data generated from the following third-party projects.

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

---

## AvaloniaUI/Avalonia

**Used for**: `src/XamlKeys.Core/AvaloniaFluentKeys.json`, `src/XamlKeys.Core/AvaloniaFluent2Keys.json`, `src/XamlKeys.Core/AvaloniaSimpleKeys.json`, `src/XamlKeys.Core/AvaloniaColorPickerKeys.json` — the key names of the Fluent, Fluent2 and Simple themes and of the ColorPicker theme. These files contain
**key names only** (no code, no styles, no values), extracted from the theme resource dictionaries at the
commit recorded in each file's `source` field. They apply only when the scanned tree merges that theme.

- Source: https://github.com/AvaloniaUI/Avalonia
- Commit: `8035f14fd356beb7d7e7d8243dc8ce98dfc27551`
- License: MIT
- Regenerate: `node scripts/regenerate-framework-keys.mjs <avalonia-fluent | avalonia-fluent2 | avalonia-simple | avalonia-colorpicker> <clone> <sha>`

```
The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ
All Rights Reserved

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

## AvaloniaUI/Avalonia.Controls.DataGrid

**Used for**: `src/XamlKeys.Core/AvaloniaDataGridKeys.json` — the key names of the DataGrid theme. These files contain
**key names only** (no code, no styles, no values), extracted from the theme resource dictionaries at the
commit recorded in each file's `source` field. They apply only when the scanned tree merges that theme.

- Source: https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid
- Commit: `ce88ee161f3cddb58bdeccabec0a4a9198bb2482`
- License: MIT
- Regenerate: `node scripts/regenerate-framework-keys.mjs <avalonia-datagrid> <clone> <sha>`

```
The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ
All Rights Reserved

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

## AvaloniaUI/Avalonia.Controls.TreeDataGrid

**Used for**: `src/XamlKeys.Core/AvaloniaTreeDataGridKeys.json` — the key names of the TreeDataGrid theme. These files contain
**key names only** (no code, no styles, no values), extracted from the theme resource dictionaries at the
commit recorded in each file's `source` field. They apply only when the scanned tree merges that theme.

- Source: https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid
- Commit: `914572f49ac4b51a2e1ea037a86ff3a494385a5f`
- License: MIT
- Regenerate: `node scripts/regenerate-framework-keys.mjs <avalonia-treedatagrid> <clone> <sha>`

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors
All Rights Reserved

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

## CommunityToolkit/Windows

**Used for**: `src/XamlKeys.Core/CommunityToolkitKeys.json` — the key names of the Windows Community Toolkit controls (including their lightweight-styling keys). These files contain
**key names only** (no code, no styles, no values), extracted from the theme resource dictionaries at the
commit recorded in each file's `source` field. They apply only when the scanned tree merges that theme.

- Source: https://github.com/CommunityToolkit/Windows
- Commit: `a2fa47ad72cb95ed81f3ff0885da627a5fd6c14e`
- License: MIT
- Regenerate: `node scripts/regenerate-framework-keys.mjs <toolkit> <clone> <sha>`

```
# Windows Community Toolkit

Copyright © .NET Foundation and Contributors

All rights reserved.

## MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
