package com.nexomarket.scanner;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.media.Image;
import android.os.Bundle;
import android.os.SystemClock;
import android.util.Log;
import android.widget.TextView;
import android.widget.EditText;

import androidx.annotation.NonNull;
import androidx.camera.core.CameraSelector;
import androidx.camera.core.ImageAnalysis;
import androidx.camera.core.ImageProxy;
import androidx.camera.core.Preview;
import androidx.camera.lifecycle.ProcessCameraProvider;
import androidx.camera.view.PreviewView;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;

import com.google.common.util.concurrent.ListenableFuture;
import com.google.mlkit.vision.barcode.BarcodeScanner;
import com.google.mlkit.vision.barcode.BarcodeScanning;
import com.google.mlkit.vision.barcode.common.Barcode;
import com.google.mlkit.vision.common.InputImage;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.io.OutputStream;

public class MainActivity extends Activity {
    private static final String TAG = "NexoMarketScan";
    private static final int CAMERA_REQUEST = 1001;
    private PreviewView preview;
    private TextView status;
    private EditText serverUrl;
    private EditText token;
    private ExecutorService executor;
    private BarcodeScanner scanner;
    private long lastCodeAt = 0;
    private String lastCode = "";

    @Override
    public void onCreate(Bundle state) {
        super.onCreate(state);
        setContentView(com.nexomarket.scanner.R.layout.activity_main);
        preview = findViewById(com.nexomarket.scanner.R.id.preview);
        status = findViewById(com.nexomarket.scanner.R.id.status);
        serverUrl = findViewById(com.nexomarket.scanner.R.id.serverUrl);
        token = findViewById(com.nexomarket.scanner.R.id.token);
        executor = Executors.newSingleThreadExecutor();
        scanner = BarcodeScanning.getClient();

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            ActivityCompat.requestPermissions(this, new String[]{Manifest.permission.CAMERA}, CAMERA_REQUEST);
        } else {
            startCamera();
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions, @NonNull int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode == CAMERA_REQUEST && results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) startCamera();
        else status.setText("Necesitás permitir la cámara para escanear.");
    }

    private void startCamera() {
        final ListenableFuture<ProcessCameraProvider> future = ProcessCameraProvider.getInstance(this);
        future.addListener(() -> {
            try {
                ProcessCameraProvider provider = future.get();
                provider.unbindAll();

                Preview cameraPreview = new Preview.Builder().build();
                cameraPreview.setSurfaceProvider(preview.getSurfaceProvider());

                ImageAnalysis analysis = new ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .build();
                analysis.setAnalyzer(executor, this::analyzeFrame);

                provider.bindToLifecycle(this, CameraSelector.DEFAULT_BACK_CAMERA, cameraPreview, analysis);
                status.setText("Cámara activa · apuntá al código de barras");
            } catch (Exception ex) {
                status.setText("No se pudo iniciar la cámara");
                Log.e(TAG, "camera", ex);
            }
        }, ContextCompat.getMainExecutor(this));
    }

    private void analyzeFrame(ImageProxy proxy) {
        Image media = proxy.getImage();
        if (media == null) { proxy.close(); return; }
        InputImage image = InputImage.fromMediaImage(media, proxy.getImageInfo().getRotationDegrees());
        scanner.process(image)
                .addOnSuccessListener(codes -> {
                    for (Barcode b : codes) {
                        String value = b.getRawValue();
                        if (value != null && value.length() >= 4) {
                            sendBarcode(value);
                            break;
                        }
                    }
                })
                .addOnCompleteListener(task -> proxy.close());
    }

    private void sendBarcode(String value) {
        long now = SystemClock.elapsedRealtime();
        if (value.equals(lastCode) && now - lastCodeAt < 1200) return;
        lastCode = value;
        lastCodeAt = now;
        Log.d(TAG, "BARCODE:" + value);
        sendToNetwork(value);
        runOnUiThread(() -> status.setText("Código enviado: " + value));
    }


    private void sendToNetwork(String value) {
        final String base = serverUrl == null ? "" : serverUrl.getText().toString().trim();
        final String auth = token == null ? "" : token.getText().toString().trim();
        if (base.length() == 0 || auth.length() == 0) return;
        executor.execute(() -> {
            HttpURLConnection connection = null;
            try {
                String urlText = base + (base.contains("?") ? "&" : "?") + "token=" + URLEncoder.encode(auth, "UTF-8") + "&code=" + URLEncoder.encode(value, "UTF-8");
                connection = (HttpURLConnection) new URL(urlText).openConnection();
                connection.setConnectTimeout(2500);
                connection.setReadTimeout(2500);
                connection.setRequestMethod("GET");
                connection.getResponseCode();
            } catch (Exception ignored) {
            } finally {
                if (connection != null) connection.disconnect();
            }
        });
    }

    @Override
    protected void onDestroy() {
        try { if (scanner != null) scanner.close(); } catch (Exception ignored) { }
        if (executor != null) executor.shutdown();
        super.onDestroy();
    }
}
