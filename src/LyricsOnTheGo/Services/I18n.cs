using System;
using System.Collections.Generic;

namespace LyricsOnTheGo.Services;

/// <summary>
/// App-wide string table (README §7). English is the default; a single switch toggles EN ⇄ ES.
/// UI surfaces subscribe to <see cref="Changed"/> and re-pull every string via <see cref="T"/>
/// when the language changes, so localization is live (no restart).
/// </summary>
public static class I18n
{
    // key → (English, Español). Copied verbatim from README §7.
    private static readonly Dictionary<string, (string En, string Es)> Table = new()
    {
        ["settingsTitle"]   = ("Settings", "Configuración"),
        ["pin"]             = ("Pin on top", "Fijar encima"),
        ["karaoke"]         = ("Karaoke (fullscreen)", "Karaoke (pantalla completa)"),
        ["resync"]          = ("Resync", "Resincronizar"),
        ["close"]           = ("Close (to tray)", "Cerrar (a la bandeja)"),
        ["grpAppearance"]   = ("Appearance", "Apariencia"),
        ["textColor"]       = ("Text color", "Color de letra"),
        ["bgColor"]         = ("Background color", "Color de fondo"),
        ["bgOpacity"]       = ("Background opacity", "Opacidad del fondo"),
        ["bgOpacityHint"]   = ("Lower opacity = more glass effect (background blur).",
                               "Menos opacidad = más efecto glass (desenfoque del fondo)."),
        ["textSize"]        = ("Text size", "Tamaño de letra"),
        ["dimInactive"]     = ("Dim inactive lines", "Atenuar líneas inactivas"),
        ["alignment"]       = ("Alignment", "Alineación"),
        ["alignLeft"]       = ("Left", "Izquierda"),
        ["alignCenter"]     = ("Center", "Centro"),
        ["grpSync"]         = ("Sync", "Sincronización"),
        ["offset"]          = ("Lyrics offset", "Desfase de la letra"),
        ["offsetHint"]      = ("Delays (+) or advances (−) the active line up to ±6 s. Useful if the lyrics are out of sync.",
                               "Retrasa (+) o adelanta (−) la línea activa hasta ±6 s. Útil si la letra va desincronizada."),
        ["grpVisibility"]   = ("Visibility", "Visibilidad"),
        ["autohide"]        = ("Auto-hide header", "Auto-ocultar encabezado"),
        ["autohideHint"]    = ("Hides the top bar. Move the mouse or click the overlay to show it for 2 s and open settings.",
                               "Oculta la barra superior. Mueve el mouse o haz clic en el overlay para mostrarla 2 s y entrar a ajustes."),
        ["showProgress"]    = ("Show progress bar", "Mostrar barra de progreso"),
        ["showTimes"]       = ("Show times (current / total)", "Mostrar tiempos (actual / total)"),
        ["grpBehavior"]     = ("Behavior", "Comportamiento"),
        ["autostart"]       = ("Start with Windows", "Iniciar con Windows"),
        ["autostartHint"]   = ("Launches the overlay automatically when you sign in to Windows.",
                               "Inicia el overlay automáticamente al iniciar sesión en Windows."),
        ["clickthrough"]    = ("Click-through (ignore clicks)", "Click-through (ignorar clics)"),
        ["clickthroughHint"]= ("This lets the overlay pass clicks through. To disable it, use the tray icon menu.",
                               "Con esto el overlay deja pasar los clics. Para desactivarlo, usa el menú del icono en la bandeja del sistema."),
        ["plainFallback"]   = ("Show plain text if no sync", "Mostrar texto plano si no hay sync"),
        ["grpCache"]        = ("Lyrics cache", "Caché de letras"),
        ["cacheHint"]       = ("Lyrics are saved to disk so they aren't downloaded again. Clear the cache to force a fresh download.",
                               "Las letras se guardan en disco para no volver a descargarlas. Limpia el caché si quieres forzar una nueva descarga."),
        ["clearCache"]      = ("Clear lyrics cache", "Limpiar caché de letras"),
        ["clearOffsets"]    = ("Clear saved offsets", "Limpiar desfases guardados"),
        ["grpLocalDb"]      = ("Local database", "Base de datos local"),
        ["localDbHint"]     = ("Link the offline LRCLIB database for instant, offline lookups.",
                               "Vincula la base de datos LRCLIB para búsquedas instantáneas y sin conexión."),
        ["localDbDownload"] = ("Download the database ↗", "Descargar la base de datos ↗"),
        ["localDbWarn"]     = ("The uncompressed database is around 117 GB.",
                               "La base de datos descomprimida pesa alrededor de 117 GB."),
        ["selectDb"]        = ("Select database file…", "Seleccionar base de datos…"),
        ["unlinkDb"]        = ("Unlink database", "Desvincular base de datos"),
        ["localDbNone"]     = ("(no database linked)", "(sin base de datos vinculada)"),
        ["localDbLinked"]   = ("Linked ✓ — used first; the API is the fallback.",
                               "Vinculada ✓ — se usa primero; la API es el respaldo."),
        ["localDbMissing"]  = ("The linked file is missing — using the API.",
                               "El archivo vinculado no existe — usando la API."),
        ["localDbInvalid"]  = ("That file isn't a valid LRCLIB database.",
                               "Ese archivo no es una base de datos LRCLIB válida."),
        ["offsetsCleared"]  = ("Offsets cleared ({n})", "Desfases limpiados ({n})"),
        ["reset"]           = ("Reset to defaults", "Restablecer predeterminados"),
        ["buyMeCoffee"]     = ("Buy me a coffee", "Cómprame un café"),
        ["openSpotify"]     = ("Play something…", "Reproduce algo…"),
        ["searching"]       = ("Searching for lyrics…", "Buscando letra…"),
        ["notfound"]        = ("Lyrics not found", "No se encontró la letra"),
        ["onlyUnsynced"]    = ("Only unsynced lyrics available (disabled in settings)",
                               "Solo hay letra sin sincronización (desactivada en ajustes)"),
        ["instrumental"]    = ("♪ Instrumental ♪", "♪ Instrumental ♪"),
        ["cacheCleared"]    = ("Cache cleared ({n})", "Caché limpiado ({n})"),
        ["clearFailed"]     = ("Clear failed", "Error al limpiar"),
        // Tray menu (README §7 note: localize the originally Spanish-hardcoded labels).
        ["trayShow"]          = ("Show", "Mostrar"),
        ["trayHide"]          = ("Hide", "Ocultar"),
        ["trayClickThrough"]  = ("Click-through", "Click-through"),
        ["trayDiagnostics"]   = ("Diagnostics…", "Diagnóstico…"),
        ["trayQuit"]          = ("Quit", "Salir"),
        // Diagnostics window (README §8).
        ["diagTitle"]         = ("LyricsOnTheGo — Diagnostics", "LyricsOnTheGo — Diagnóstico"),
        ["diagProviders"]     = ("Lyrics providers", "Proveedores de letra"),
        ["diagProvidersHint"] = ("Turn a source on/off live. Disabling a bad source stops it from being used immediately.",
                                 "Activa/desactiva una fuente en vivo. Al desactivar una fuente mala deja de usarse de inmediato."),
        ["diagClear"]         = ("Clear log", "Limpiar registro"),
        ["diagAutoscroll"]    = ("Auto-scroll", "Auto-desplazar"),
        ["diagColTime"]       = ("Time", "Hora"),
        ["diagColTrack"]      = ("Track", "Canción"),
        ["diagColProvider"]   = ("Provider", "Proveedor"),
        ["diagColResult"]     = ("Result", "Resultado"),
        ["diagColDetail"]     = ("Match / detail", "Coincidencia / detalle"),
        ["diagColMs"]         = ("ms", "ms"),
        ["diagWaiting"]       = ("Waiting for the next song change…", "Esperando el siguiente cambio de canción…"),
        ["diagSource"]        = ("Now playing from", "Reproduciendo desde"),
        ["diagSourceNone"]    = ("no active session", "sin sesión activa"),
        ["diagSourceBrowser"] = ("browser (title-only search)", "navegador (búsqueda solo por título)"),
        ["diagTrackDuration"] = ("track duration", "duración de la canción"),
    };

    /// <summary>Current language: "en" (default) or "es".</summary>
    public static string Lang { get; private set; } = "en";

    /// <summary>Raised after the language changes so UI can re-localize.</summary>
    public static event Action? Changed;

    public static void SetLanguage(string? lang)
    {
        string normalized = lang == "es" ? "es" : "en";
        if (normalized == Lang)
            return;
        Lang = normalized;
        Changed?.Invoke();
    }

    /// <summary>Look up a string in the current language. Unknown keys return the key itself.</summary>
    public static string T(string key)
    {
        if (Table.TryGetValue(key, out var pair))
            return Lang == "es" ? pair.Es : pair.En;
        return key;
    }
}
