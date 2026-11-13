# NetTT · diseño del estudio de llamadas

Entrega de diseño de interfaz y arquitectura, 7 de noviembre de 2026.

Abre `index.html` en un navegador. No requiere servidor, dependencias, permisos de captura ni conexión. Es un prototipo interactivo: todos los participantes, capacidades y resultados son simulados. No modifica la configuración de la aplicación.

- Cambia entre perfiles de conversación, juego, cine y presentación.
- Configura pantalla, cámara y micrófono de forma independiente.
- Explora vídeo, voz, latencia, compatibilidad y arquitectura.
- Compara el borrador con la configuración aplicada en la simulación.
- Prueba un fallo de aplicación: conserva el estado anterior y el borrador.
- Exporta el perfil como JSON; guarda y recupera un perfil en este navegador.

`architecture.md` contiene el inventario del código actual, contratos propuestos, negociación, buffers, sincronización, cambios en directo, migración y criterios de aceptación. Los rangos del prototipo son propuestas de producto, no una declaración de capacidades del equipo.

El prototipo sigue siendo una simulación. La implementación nativa vive en los proyectos de la aplicación y se documenta en `architecture.md`; utiliza FFmpeg y superficies D3D11/Android, con pruebas de capacidades por participante. No utilizar las Quest del usuario para ejecutar pruebas.

Verificación: `node doc/design/nettt-call-studio/check.mjs` (requiere el Playwright del repositorio). Capturas regenerables en `.artifacts/nettt-call-studio/`.
