/*
 * Ejemplo mínimo de puente Android para NexoMarket.
 * El escaneo real puede entregar el resultado de cualquier lector de códigos
 * instalado en el teléfono. La línea enviada al PC debe ser BARCODE:<codigo>.
 */
package com.nexomarket.scanner;

import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;

public class MainActivity extends Activity {
    private static final String TAG = "NexoMarketScan";

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        final EditText code = new EditText(this);
        code.setHint("Código de barras");
        Button send = new Button(this);
        send.setText("ENVIAR AL NEXOMARKET");
        send.setOnClickListener(v -> {
            String value = code.getText().toString().trim();
            if (value.length() > 0) Log.d(TAG, "BARCODE:" + value);
        });
        box.addView(code);
        box.addView(send);
        setContentView(box);
    }
}
