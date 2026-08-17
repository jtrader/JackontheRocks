Branding and Theme

This admin UI supports a custom theme and logo. To apply your real brand assets:

1. Replace `public/admin/assets/logo.zip` with the provided `Logo.zip` and extract its contents in this folder. Alternatively extract locally and copy the relevant images into `public/admin/assets/`.

2. Recommended files:
   - `logo.png` - site logo used in the admin header
   - `favicon.ico` - site favicon
   - `theme.json` or a simple `branding.json` containing primary/accent colors.

3. The admin UI will read `assets/logo.png` for the header. Thumbnails and previews will show in the video list when available.

Unity side:

Place the hero logo and `branding.json` under `Assets/StreamingAssets/branding/`:
 - `Assets/StreamingAssets/branding/logo.png`
 - `Assets/StreamingAssets/branding/branding.json`

The `JackOnTheRocksBrandingManager` singleton will load `branding.json` and `logo.png` at runtime and expose colors and `logoSprite` for UI usage. Use `ApplyToImage(image)` to set a UI Image to the loaded logo.
