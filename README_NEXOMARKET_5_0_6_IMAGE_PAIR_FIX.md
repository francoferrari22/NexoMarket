# NexoMarket 5.0.6 — Image + Windows pairing fix

- Windows pairing now uses a short 8-digit one-time code stored as a hash in PostgreSQL.
- Existing pairing tokens remain accepted for compatibility.
- Codes accept spaces/hyphens and expire after 10 minutes.
- Media URLs are relative `/media/...`, so image display no longer depends on PUBLIC_BASE_URL or R2_PUBLIC_BASE_URL.
- Existing R2 `/stores/...` image URLs are normalized to the local `/media/stores/...` proxy.
- Products without an image receive an automatically generated category placeholder served by NexoMarket.
- Do not create a new PostgreSQL database or change the existing Store ID.
