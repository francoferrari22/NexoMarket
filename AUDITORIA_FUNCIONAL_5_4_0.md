# Auditoría funcional NexoMarket 5.4.0

## Prioridad URGENTE/BLOQUEANTE

| Área | Implementación |
|---|---|
| Comprobante automático | Cola SMTP + comprobante HTML |
| Bienvenida | Email al registrar cuenta |
| Cambio de estado | Email transaccional |
| Pago aprobado/rechazado | Endpoint de estado + email |
| Webhook | HMAC-SHA256 + secreto configurable |
| Idempotencia | `idempotencyKey` en creación de pedidos |
| Stock | Serialización y validación antes de descontar |
| Cancelación | Restaura stock y cupón |
| Reembolso | Estado Reembolsado + restitución |
| Recuperación de contraseña | Código, hash, expiración y respuesta uniforme |
| Operaciones vendedor | `syncKey` obligatorio en pending/ack/status |
| Auditoría | Registro persistente y acceso Super Admin |

## IMPORTANTE

| Función | Estado |
|---|---|
| Historial comprador | Existente |
| Seller Center | Existente |
| Gestión de productos | Existente |
| Promociones/cupones | Existente |
| Seguimiento por estado | Existente/reforzado |
| Super Admin | Existente/reforzado |
| Almacenamiento R2 | Existente |
| PostgreSQL | Existente |
| Recuperación de carrito por email | No conectada todavía al frontend |
| Push/WhatsApp | No implementado |
| Facturación fiscal ARCA | Requiere integración fiscal y credenciales reales |
| Devolución monetaria automática | Requiere API del proveedor de pagos |

## NICE TO HAVE

- Recomendaciones.
- Segmentación.
- Predicción de demanda.
- Fidelización.
- Tracking geográfico.
- Motor avanzado de promociones.
- Centro de soporte integrado.

## Prueba de aceptación recomendada

1. Crear cuenta.
2. Recibir bienvenida.
3. Crear tienda.
4. Publicar producto con stock 1.
5. Crear pedido.
6. Repetir el mismo `idempotencyKey`: debe devolver el mismo pedido.
7. Intentar segundo pedido del mismo producto: debe rechazar por stock.
8. Confirmar pago mediante webhook firmado.
9. Cambiar estado a Preparando/Listo/Enviado/Entregado.
10. Verificar emails.
11. Cancelar un pedido no entregado y comprobar stock.
12. Registrar reembolso y comprobar estado.
13. Consultar auditoría desde Super Admin.
14. Solicitar recuperación de contraseña.
15. Restablecer con código válido.
16. Repetir webhook: no debe duplicar acciones.

## Límite de esta versión

El sistema comercial y el comprobante no equivalen automáticamente a una factura fiscal argentina. Para producción fiscal hay que conectar el flujo de facturación a ARCA o un proveedor autorizado y conservar los datos fiscales correspondientes.
