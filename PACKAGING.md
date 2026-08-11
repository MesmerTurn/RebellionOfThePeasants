# How this addon is actually packaged (read before touching SubModule.xml)

`MakeBltGreatAgain.dll` and `PeasantRebellionPerks.dll` are **not** separate top-level
Bannerlord modules. There is no `Modules\MakeBltGreatAgain\` or
`Modules\PeasantRebellionPerks\` folder in the real, Nexus-published package, and the
launcher shows only **one** entry for all of this: **BLTAdoptAHero**.

Both DLLs are physically copied into `BLTAdoptAHero\bin\Win64_Shipping_Client\`, and
their sub-modules are registered as `<SubModule>` entries **inside**
`BLTAdoptAHero`'s own `SubModule.xml` — not their own `<Module>`. That file lives in
the core fork repo, not here:

```
<fork repo>/BLTAdoptAHero/_Module/SubModule.xml
```

(Confirmed 2026-08-11 by inspecting an actual Nexus-downloaded release zip — it
contains only `BLTAdoptAHero`, `BLTBuffet`, `BLTConfigure`, `Bannerlord.ButterLib`,
`BannerlordTwitch` as top-level folders, with `MakeBltGreatAgain.dll` sitting inside
`BLTAdoptAHero\bin\Win64_Shipping_Client\`.)

## When adding a new sub-module (in either `source/MakeBltGreatAgain.cs` or
## `source_perks/PeasantRebellionPerks.cs`)

1. Write the `MBSubModuleBase` subclass as usual.
2. Add a `<SubModule>` entry for it to `BLTAdoptAHero\_Module\SubModule.xml` in the
   fork repo — **not** a new file here.
3. When staging a release package, copy the built DLL into the staged
   `BLTAdoptAHero\bin\Win64_Shipping_Client\` folder, alongside `BLTAdoptAHero.dll`
   itself — do not create a separate module folder for it.
