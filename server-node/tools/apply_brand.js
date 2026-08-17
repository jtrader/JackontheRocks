const fs = require('fs');
const path = require('path');
const unzipper = require('unzipper');

async function extractZip() {
  const repoRoot = path.resolve(__dirname, '..');
  const adminAssets = path.join(repoRoot, 'public', 'admin', 'assets');
  const zipPath = path.join(adminAssets, 'logo.zip');
  const unityBranding = path.join(repoRoot, '..', 'Assets', 'StreamingAssets', 'branding');

  if (!fs.existsSync(zipPath)) {
    console.error('logo.zip not found at', zipPath);
    process.exit(1);
  }

  if (!fs.existsSync(adminAssets)) fs.mkdirSync(adminAssets, { recursive: true });
  if (!fs.existsSync(unityBranding)) fs.mkdirSync(unityBranding, { recursive: true });

  console.log('Extracting', zipPath);
  fs.createReadStream(zipPath)
    .pipe(unzipper.Parse())
    .on('entry', async function (entry) {
      const fileName = entry.path;
      const type = entry.type; // 'Directory' or 'File'
      const ext = path.extname(fileName).toLowerCase();
      try {
        if (type === 'File') {
          // normalize target filenames
          const base = path.basename(fileName);
          // write to admin assets
          const targetAdmin = path.join(adminAssets, base);
          entry.pipe(fs.createWriteStream(targetAdmin));
          console.log('Wrote admin asset:', targetAdmin);
          // if image, also copy to Unity branding
          if (['.png', '.jpg', '.jpeg', '.svg', '.webp'].includes(ext)) {
            const targetUnity = path.join(unityBranding, base);
            // delay copy until stream finishes
            entry.on('end', () => {
              try {
                fs.copyFileSync(targetAdmin, targetUnity);
                console.log('Copied to Unity branding:', targetUnity);
              } catch (e) { console.warn('Copy to unity failed:', e.message); }
            });
          }
        } else {
          entry.autodrain();
        }
      } catch (e) {
        console.warn('Entry handling failed', e.message);
        entry.autodrain();
      }
    })
    .on('close', () => { console.log('Extraction complete.'); })
    .on('error', (err) => { console.error('Extraction error:', err); process.exit(1); });
}

extractZip();
