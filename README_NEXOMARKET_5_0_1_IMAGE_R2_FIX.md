# NexoMarket 5.0.1 — R2 image delivery fix

This build fixes product images when R2 does not have a public bucket/domain URL.

## What changed
- Uploaded images are stored in Cloudflare R2 as before.
- The upload API now returns a NexoMarket URL based on `PUBLIC_BASE_URL`:
  `/media/<r2-key>`.
- `GET /media/<r2-key>` reads the object from R2 and streams it to the browser.
- Catalog JSON no longer falls back to a Windows-local `ImagePath`.
- Upload fails explicitly if `PUBLIC_BASE_URL` is missing, instead of silently returning an unusable image URL.

## Render requirements
Keep these environment variables configured:
- `PUBLIC_BASE_URL`
- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET`

`R2_PUBLIC_BASE_URL` is no longer required for product images in this build.

## Important
After deploying this build, publish/sync the products again from the Windows Admin so their `WebImageUrl` is replaced with the new `/media/...` URL.
