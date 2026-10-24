# Passive Component Standard Symbol Templates (`Standard_Passives.SchLib`)

This directory is used when the **Passive Symbol Template Filter** (*"Use Standard Symbol for Passives (R/C/L)"*) option is enabled in EasyAltium.

## Supported Standard Symbol References (`PassiveTemplateLibRef`)
- `RES_STD` : Standard Resistor Symbol (2 Pins: `1`, `2`)
- `CAP_NP_STD` : Non-Polarized Capacitor Symbol (2 Pins: `1`, `2`)
- `CAP_POL_STD` : Polarized / Electrolytic Capacitor Symbol (2 Pins: `1`, `2`)
- `IND_STD` : Standard Inductor / Ferrite Bead Symbol (2 Pins: `1`, `2`)

When you place your custom `Standard_Passives.SchLib` file in this directory (or select a custom path in the Settings window), `Bridge.pas` loads the schematic symbol graphic from your template while automatically attaching the **PCB Footprint**, **3D STEP Model**, and **LCSC/JLCPCB BOM Parameters** from the fetched LCSC component.
