# NexoMarket 5.4.0 — Endurecimiento de producción

Esta versión parte de `NexoMarket 5.3.0` y conserva el proyecto sin datos de tiendas/cuentas.

## Implementado

### 1. Transacciones y comunicaciones
- Cola de correo transaccional en segundo plano.
- Bienvenida al crear cuenta.
- Confirmación de pedido.
- Confirmación/rechazo de pago.
- Cambio de estado del pedido.
- Cancelación.
- Reembolso registrado.
- Comprobante HTML accesible mediante `/api/orders/receipt`.
- Recuperación de contraseña con código de un solo uso y vencimiento de 30 minutos.
- Los errores del SMTP no bloquean checkout ni creación de pedidos.

### 2. Gaps operativos
- Idempotencia de creación de pedidos mediante `idempotencyKey`.
- Historial central de pedidos existente y reforzado.
- Estados normalizados.
- Cancelación y devolución lógica.
- Auditoría de acciones.
- Panel Super Admin separado conservado y ampliado con consulta de auditoría.
- Control de activación/prueba/cuentas/tiendas.

### 3. Edge cases y seguridad
- Creación de pedidos serializada para evitar carreras de stock.
- Validación de stock antes de descontar.
- Restauración de stock al cancelar/reembolsar.
- Restauración del uso de cupón al cancelar.
- Webhooks de pago con HMAC:
  - POST `/api/payments/webhook`
  - Header `X-Nexo-Payment-Signature`
  - Variable `NEXOMARKET_PAYMENT_WEBHOOK_SECRET`
- Reenvío de webhooks tolerado por estados idempotentes.
- Endpoints operativos de pedidos de la tienda protegidos por `syncKey`.
- Auditoría persistente de operaciones.
- Recuperación de contraseña sin revelar si un correo existe.
- No se guardan contraseñas ni claves SMTP en el código.

### 4. Valor agregado preparado
- Seguimiento por estados.
- Comprobante transaccional.
- Auditoría central.
- Base preparada para analítica y notificaciones.
- Super Admin con control total.

## Configuración de correo en Render

Definir:
- `NEXOMARKET_SMTP_HOST`
- `NEXOMARKET_SMTP_PORT` (por defecto 587)
- `NEXOMARKET_SMTP_USER`
- `NEXOMARKET_SMTP_PASSWORD`
- `NEXOMARKET_SMTP_FROM`
- `NEXOMARKET_SMTP_SSL=1`

El correo se envía mediante una cola local para que un fallo del SMTP no derribe el flujo de compra.

## Configuración de pagos

Definir:
- `NEXOMARKET_PAYMENT_WEBHOOK_SECRET`

El proveedor de pagos debe firmar el cuerpo HTTP completo con HMAC-SHA256 y enviar el resultado Base64 en `X-Nexo-Payment-Signature`.

Ejemplo conceptual de evento:
```json
{
  "storeId": "TIENDA",
  "centralOrderId": "PEDIDO",
  "paymentStatus": "approved",
  "paymentReference": "PROVEEDOR-123"
}
```

Los valores aceptados se normalizan a:
`Pendiente`, `Aprobado`, `Rechazado`, `Reembolsado`.

## Facturación legal

El comprobante NexoMarket NO debe presentarse como factura fiscal legal. La facturación legal en Argentina depende del régimen fiscal y de la integración con ARCA/proveedor autorizado, certificado, punto de venta, tipo de comprobante y CAE. Esta versión deja separado el comprobante comercial del proceso fiscal para evitar emitir documentación ilegalmente válida sin credenciales/configuración fiscal.

## Super Administrador

Se mantiene:
`NexoMarket.SuperAdmin`

La clave maestra se configura exclusivamente con:
`NEXOMARKET_ADMIN_KEY`

El nuevo botón `AUDITORÍA / SEGURIDAD` consulta:
`GET /api/audit`
con header:
`X-Nexo-Admin-Key`

## Recomendación de despliegue

1. Configurar PostgreSQL.
2. Configurar R2.
3. Configurar `NEXOMARKET_ADMIN_KEY`.
4. Configurar SMTP.
5. Configurar `NEXOMARKET_PAYMENT_WEBHOOK_SECRET`.
6. Desplegar.
7. Crear una tienda de prueba desde Super Admin.
8. Ejecutar una compra de prueba.
9. Comprobar correo.
10. Comprobar webhook.
11. Cancelar y comprobar restitución de stock.
12. Revisar auditoría.
13. Repetir el mismo `idempotencyKey` y comprobar que no se crea un segundo pedido.

## Importante

No se incluyen tiendas, cuentas, credenciales ni datos productivos en este paquete.
