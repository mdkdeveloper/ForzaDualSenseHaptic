# Forza × DualSense application icon

The selected hybrid icon is original vector artwork inspired by the Forza racing emblem and DualSense silhouette, not an extracted official brand asset. It is embedded as both the Windows executable icon and the Avalonia window icon.

- `hybrid.svg`: the racing emblem joins the right half of a controller.
- `preview.png`: a large preview plus native 16, 32, and 48 px samples.
- `hybrid.png`: 512 px PNG; individual 16/24/32/48/64/128/256 px PNGs and a multi-resolution ICO containing those seven sizes are also provided.
- `../../ForzaHaptics/Assets/app.ico`: the application resource, copied from the canonical `hybrid.ico` by the renderer.

Regenerate with Node.js and `sharp` installed:

```powershell
node design/icons/render.cjs
```

If `sharp` is installed outside the repository, set `NODE_PATH` to its parent `node_modules` directory. The script reads the SVG original, regenerates every raster asset and the preview sheet, and updates the application icon resource. No external fonts or images are embedded in the icon. The preview sheet uses the available Segoe UI or Arial font.
