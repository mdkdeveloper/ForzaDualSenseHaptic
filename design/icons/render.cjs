// Run: node design/icons/render.cjs (requires sharp; NODE_PATH may point to its installation).
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
const dir = __dirname;
const sizes = [16, 24, 32, 48, 64, 128, 256];

async function main() {
  const layers = [];
  for (const name of ['hybrid']) {
    const svg = fs.readFileSync(path.join(dir, name + '.svg'));
    const png = await sharp(svg).resize(512, 512).png().toBuffer();
    fs.writeFileSync(path.join(dir, name + '.png'), png);
    const frames = await Promise.all(sizes.map(size => sharp(svg).resize(size, size).png().toBuffer()));
    const header = Buffer.alloc(6 + 16 * frames.length);
    header.writeUInt16LE(1, 2);
    header.writeUInt16LE(frames.length, 4);
    let offset = header.length;
    frames.forEach((frame, i) => {
      const entry = 6 + i * 16;
      header[entry] = header[entry + 1] = sizes[i] === 256 ? 0 : sizes[i];
      header.writeUInt16LE(1, entry + 4);
      header.writeUInt16LE(32, entry + 6);
      header.writeUInt32LE(frame.length, entry + 8);
      header.writeUInt32LE(offset, entry + 12);
      offset += frame.length;
      fs.writeFileSync(path.join(dir, `${name}-${sizes[i]}.png`), frame);
    });
    fs.writeFileSync(path.join(dir, name + '.ico'), Buffer.concat([header, ...frames]));
    const appIcon = path.resolve(dir, '../../ForzaHaptics/Assets/app.ico');
    fs.mkdirSync(path.dirname(appIcon), { recursive: true });
    fs.copyFileSync(path.join(dir, name + '.ico'), appIcon);
    const left = 64;
    layers.push({ input: await sharp(svg).resize(360, 360).png().toBuffer(), left, top: 134 });
    for (const [i, size] of [16, 32, 48].entries()) {
      layers.push({ input: await sharp(svg).resize(size, size).png().toBuffer(), left: left + 20 + i * 122, top: 594 - Math.floor(size / 2) });
    }
  }
  const card = Buffer.from(`<svg width="488" height="740" xmlns="http://www.w3.org/2000/svg">
    <rect width="488" height="740" fill="#101218"/>
    <g font-family="Segoe UI, Arial, sans-serif" fill="#f4f5f9">
      <text x="64" y="57" font-size="25" font-weight="600">FORZA × DUALSENSE</text>
      <text x="64" y="88" font-size="15" fill="#929bae">Application icon · Original vector artwork</text>
      <text x="64" y="533" font-size="22" font-weight="600">Hybrid mark</text>
      <g font-size="13" fill="#929bae">
        <text x="64" y="558">A racing wing becomes a controller.</text>
        <text x="79" y="646">16 px</text><text x="201" y="646">32 px</text><text x="323" y="646">48 px</text>
        <text x="64" y="701">Native-size previews · SVG / PNG / ICO</text>
      </g>
    </g>
  </svg>`);
  await sharp(card).composite(layers).png().toFile(path.join(dir, 'preview.png'));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
