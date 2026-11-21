# Mapa de desarrollo de QuicNet

Ejecuta los comandos desde la raíz. Sigue la fila de la función afectada y añade las comprobaciones de sus consumidores cuando cambie un contrato.

| Cambio | Punto de entrada y propietario del estado | Contrato | Suites para empezar |
| --- | --- | --- | --- |
| Navegación, contacto e invitaciones | [Página principal](../../QuicNet.Shared/wwwroot/index.html) y [contactos](../../QuicNet.Shared/wwwroot/dashboard.js) | Navegación, foco y API de pares | Chromium `ContactHomeBrowserTests.mjs`, `ConnectionModesBrowserTests.mjs` |
| Chat | [Interfaz de chat](../../QuicNet.Shared/wwwroot/chat.js), [borradores](../../QuicNet.Shared/wwwroot/chat-drafts.js) y [API de chat](../../QuicNet.Shared/WebUi/Modules/ChatApiModule.cs). [El historial](../../QuicNet.Shared/WebUi/ChatHistoryStore.cs) conserva los mensajes | `/api/chat-send`, recursos por contacto y eventos WebSocket | Chromium `ChatIsolationBrowserTests.mjs`, `ChatOutboxBrowserTests.mjs` |
| Voz y pantalla | [Interfaz de voz](../../QuicNet.Shared/wwwroot/voice.js), [selector de pantalla](../../QuicNet.Shared/wwwroot/screen-source-picker.js), [API de voz](../../QuicNet.Shared/WebUi/Modules/VoiceApiModule.cs) y [sesión de llamada](../../QuicNet.Shared/Protocols/VoiceCallHandler.cs) | Señales, permisos de pantalla y recuperación de llamada | Chromium `VoiceRoomBrowserTests.mjs`, `ScreenViewerBrowserTests.mjs`. C# `--test-voice-mobility`, `--test-screenshare` |
| Estado y contratos WebUI | [Servidor](../../QuicNet.Shared/WebUi/WebUiServer.cs), [ensamblado del estado](../../QuicNet.Shared/WebUi/Modules/StatusApiModule.cs), [proyección de contactos](../../QuicNet.Shared/WebUi/Modules/StatusPeerProjection.cs) y [contratos JSON](../../QuicNet.Shared/Serialization/) | `/api/status`, `status.get` y `status_updated` | C# `--test-webui-security`, `--test-webui-status`, `--test-aot-json`. JS `WebTransportLifecycleWebTests.mjs` |
| Red y recuperación | [Ciclo de vida](../../QuicPunch/QuicPunch.Lifecycle.cs), [registro de pares](../../QuicPunch/Discovery/AvailablePeerRegistry.cs) y [multiplexor](../../QuicPunch/Multiplexer/QuicPeerMultiplexer.cs) | Identidad, autorización por protocolo, cancelación y compatibilidad WAN/Tor | C# `--test-peer-state`, `--test-peer-registry`, `--test-connection-recovery`, `--test-session-recovery`, `--test-multiplexer` |
| Escritorio y Android | [Arranque de escritorio](../../QuicNet/Program.cs) y [servicio Android](../../QuicPunch.AndroidProbe/AndroidConnectionService.cs) | Permisos, segundo plano, WebView y cierre de recursos | Compila el host afectado y prueba el flujo. Para vídeo, C# `--test-platform-video` |
| VPN y QuicWatch | [Adaptadores VPN](../../QuicPunch.Vpn/) y [montaje y cierre de QuicWatch](../../QuicNet.Shared/Watch/WatchApplication.cs) | Política de VPN, sesiones y transferencia de medios | C# `--test-vpn-policy`, `--test-watch-reliability` |
| Actualizaciones y publicación | [Actualizador](../../QuicNet.Shared/Updates/AutoUpdateService.cs), [versión](../../Release.props) y [compilador nativo](../../QuicPunch.NativeBuilder/Program.cs) | Firma, empaquetado y activos nativos por plataforma | C# `--test-updates`, `--test-pqc`. Chromium `DownloadPageBrowserTests.mjs` |

```powershell
npm run test:web
node scripts/test-web.mjs --browser --filter=ContactHome --filter=ConnectionModes
./scripts/test.ps1 -Tests webui-security,aot-json
dotnet run --project QuicPunchTests -c Test -- --list
```

Para una revisión de interfaz, usa el preview de Playwright con el flujo afectado y comprueba teclado y móvil si el cambio alcanza esos recorridos. Para cambios compartidos, verifica el host real cuando cambie la integración. Para Linux, Android, medios y publicación consulta los requisitos de las guías.

[Guía de pruebas](../testing/test-suite.md) · [Arquitectura](overview.md) · [Actualizaciones](../deployment/auto-updates.md) · [Plan de mantenimiento](plan-mantenimiento-ia.md)

