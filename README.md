# obliq
Obliq plug-in for Rhinoceros®. Provides utilities for working with oblique (military, cabinet, etc...) projections in a non-destructive way.

# Installation (windows)
1. Download RHP file from [https://github.com/criticalsoftware-lab/obliq/releases]
2. Place in your Rhino 8 installation folder (C:\Program Files\Rhino 8\Plug-ins\)
3. Make sure you have the latest Rhino 8 service release.
4. Drag the RHP file from the Plug-ins folder into the Rhino viewport.

# Usage
**Commands:**

**Obliq** -> Creates new custom viewport that displays a perfect oblique projection aligned on the Z axis. Also known as a "military" projection or plan oblique.

**ObliqueMake2D** -> Generates a flat, 2D hidden-line drawing of the selected items (might take some time, depending on the amount of geometry).

**ObliqueCurveMake2D** -> Takes a selection of curves and applies an oblique projection, flattening them onto the CPlane.
