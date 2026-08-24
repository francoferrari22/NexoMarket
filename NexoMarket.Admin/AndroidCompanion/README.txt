NEXOMARKET SCANNER ANDROID

OBJETIVO
- La PC Windows detecta el teléfono por ADB.
- NexoMarket inicia automáticamente esta aplicación.
- La aplicación abre la cámara trasera.
- ML Kit detecta EAN/UPC/Code 128 y otros formatos compatibles.
- Cada lectura se escribe en logcat como: BARCODE:<codigo>
- NexoMarket recibe ese código y lo agrega directamente al TICKET DE VENTA.
- NO hay checklist ni catálogo en la aplicación Android.

COMPILACIÓN
1. Abrir esta carpeta como proyecto Android Studio.
2. Usar JDK 8.
3. Gradle/Android Gradle Plugin: 4.2.2 / Gradle 6.7.1.
4. Compilar app -> APK.
5. Instalar el APK en el teléfono.
6. Activar Depuración USB y aceptar la clave RSA de la PC.

NOTA
El Android Companion es independiente del requisito de compatibilidad del panel de escritorio. El panel Windows sigue siendo WinForms + .NET Framework 4.8.
