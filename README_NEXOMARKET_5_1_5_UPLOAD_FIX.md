# NexoMarket 5.1.5 – Upload de imágenes/productos

## Corrección

Se corrigió el flujo del Seller Center que podía quedar indefinidamente en “Subiendo archivos”.

- Las fotos se redimensionan y comprimen en el navegador antes de enviarse.
- Se usa Base64URL para evitar corrupción por `+`, `/` y `=` dentro de `application/x-www-form-urlencoded`.
- El servidor admite hasta 16 MB de request y 60 s de lectura/escritura.
- Las imágenes se limitan a 2,5 MB después de optimización; los videos mantienen hasta 8 MB.
- Si falla R2, el panel devuelve un error visible en lugar de quedar esperando.
- Guardar producto continúa funcionando sin foto/video.

## Requisitos

R2 debe estar configurado en Render (`R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET`).
