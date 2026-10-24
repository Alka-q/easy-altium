# EasyAltium — LCSC & JLCPCB Library Loader for Altium Designer

**EasyAltium** is an open-source, SamacSys-style desktop companion and Altium Designer bridge that imports electronic components from **LCSC** and **JLCPCB / EasyEDA** (**Schematic Symbol, PCB Footprint, 3D STEP Model with automatic Euler orientation correction, and full BOM Parameters**) directly into any version of **Altium Designer (64-bit)** with a single click or via automatic Windows clipboard monitoring.

---

## Key Features

- **Version-Agnostic Altium Designer Integration:** Automatically discovers installed Altium Designer versions (`AD*` / `X2.EXE`) and injects a pure-white vector/bitmap toolbar icon directly into Altium's **Quick Access Bar**, **Active Bar** (`TopBarSchematic` & `TopBarPCB`), and **System Toolbar** (`DXPToolbar`) via `DXP.RCS` with automatic self-healing on Altium exit.
- **Smart Clipboard Watcher (`Win32 AddClipboardFormatListener`):** Detects copied LCSC part numbers matching `^C[1-9]\d{2,}$` (e.g., `C2040`, `C14663`, `C311983`) or LCSC product URLs and displays a bottom-right preview toast (`PartPreviewPopup`) with live stock, pricing, package, and JLCPCB `Basic`/`Extended` SMT class.
- **Automatic 3D STEP Orientation Fixer (`PcbLib3DModelPatcher`):** Reads EasyEDA `outline3D` (`SVGNODE -> c_rotation`) Euler angles (`ROTX`, `ROTY`, `ROTZ`) and patches generated `.PcbLib` files in-place (`Library/Models/Data` and `Footprint/Data`) while preserving exact OLE Compound File binary stream offsets.
- **Dual Library Management Modes:**
  - **Mode A (`MasterLibraryManager`):** Rapid prototyping mode using consolidated `LCSC_Master.SchLib` and `LCSC_Master.PcbLib` libraries.
  - **Mode B (`SqliteDbLibManager` & `DbLibFileGenerator`):** Production-grade categorized SQLite database (`lcsc_components.sqlite` across `Resistors`, `Capacitors`, `Inductors`, `ICs`, `Connectors`, `Discrete`, and `Misc` tables) paired with an auto-generated `EasyAltium_LCSC.DbLib` file.
- **Passive Symbol Template Filter (`PassiveSymbolFilter`):** Optionally binds standard local symbols (`RES_STD`, `CAP_NP_STD`, `CAP_POL_STD`, `IND_STD`) for 2-pin passives while keeping the LCSC footprint, 3D STEP model, and BOM parameters.
- **Interactive Cursor Placement (`Bridge.pas`):** Loads libraries via `IntegratedLibraryManager`, instantiates the component via `SchServer`, attaches the footprint and BOM parameters, and places the component directly onto the user's schematic cursor (`Sch:MoveSelectedObjects`).

---

## Architecture Overview

```text
EasyAltium/
├── altium-bridge/
│   ├── Bridge.pas                 # Altium Designer DelphiScript bridge (CLI & Toolbar entry points)
│   ├── LCSC_Bridge.PrjScr         # Altium Script Project
│   ├── Templates/                 # Optional Standard_Passives.SchLib template directory
│   └── assets/                    # Pure-white toolbar icons (.svg, .png, .bmp)
├── ipc/
│   ├── appsettings.example.json   # Configuration template (auto-created as appsettings.json at runtime)
│   └── current_part.example.ini   # Host <-> Altium IPC data contract sample
├── Library/
│   ├── SchLib/                    # Exported Schematic Libraries (.SchLib)
│   ├── PcbLib/                    # Exported & 3D-patched PCB Footprint Libraries (.PcbLib)
│   ├── STEP/                      # Downloaded 3D STEP models (.step)
│   ├── DbLib/                     # Categorized SQLite database & EasyAltium_LCSC.DbLib
│   └── Fallback/                  # ASCII fallback documents (.SchDoc / .PcbDoc)
├── src/
│   ├── EasyAltium.Core/           # Core engine, API client, 3D patcher, SQLite DbLib & IPC managers
│   └── EasyAltium.Host/           # .NET 8 WPF desktop client (System Tray, Mini Window, Toast Popup)
├── tests/
│   └── EasyAltium.Tests/          # Unit and integration test suite
└── tools/
    └── npnp/                      # External npnp CLI binary location & license (LICENSE_npnp.txt)
```

---

## Prerequisites & Setup

1. **.NET 8.0 SDK** (Windows x64)
2. **Altium Designer** (64-bit, any modern version such as AD24 / AD25 / AD26+)
3. **`npnp` CLI Binary:**
   - Download `npnp.exe` (`v1.2.0+`) from [ref42/npnp](https://github.com/ref42/npnp/releases) and place it at:
     `tools/npnp/npnp.exe` (or ensure `npnp` is available in your system `PATH`).
4. **(Optional for Mode B `.DbLib`):** Install the 64-bit **SQLite3 ODBC Driver** (`sqliteodbc_w64.exe`) if opening `EasyAltium_LCSC.DbLib` directly inside Altium's Components panel.

---

## Build & Run

```powershell
# 1. Build the solution
dotnet build EasyAltium.sln -c Release

# 2. Run the unit & integration test suite
dotnet run --project tests/EasyAltium.Tests/EasyAltium.Tests.csproj

# 3. Launch the EasyAltium desktop client
dotnet run --project src/EasyAltium.Host/EasyAltium.Host.csproj
```

### Altium CLI Trigger Command Format
```powershell
"C:\Program Files\Altium\AD26\X2.EXE" -RScriptingSystem:RunScript(ProjectName="<RepoRoot>\altium-bridge\LCSC_Bridge.PrjScr"|ProcName="Bridge.pas>PlaceOrLoadComponent")
```

---

## License & Mandatory Attribution

This project is released under the **EasyAltium Open-Source Attribution & Non-Commercial Share-Alike License v1.0** (see [`LICENSE`](./LICENSE)).

### Attribution Requirement
Any use, copy, fork, distribution, or derivative work **must** include visible attribution in your documentation and user interface:

1. **EasyAltium by Osman Ceylan (Alka-q)** — [https://github.com/Alka-q/EasyAltium](https://github.com/Alka-q/EasyAltium)
2. **npnp by Ref42** (Underlying EasyEDA/LCSC conversion CLI engine) — [https://github.com/ref42/npnp](https://github.com/ref42/npnp) (see [`tools/npnp/LICENSE_npnp.txt`](./tools/npnp/LICENSE_npnp.txt))
