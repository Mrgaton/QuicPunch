# Validación del plan de mantenimiento de QuicNet

Revisión de trabajo de `terror` sobre `d7abdf2`, con los cambios locales incorporados en la compilación. Identidad de aplicación `1.1.27 (30)`. Windows usó SDK `11.0.100`, Node `24.14.1` y Playwright `1.64.1`. Linux se compiló en WSL con el SDK fijado por el script de distribución. Los logs regenerables están en `.artifacts/test-output/` y los paquetes locales bajo `bin/updates/`.

## Cambios aplicados

| Hito | Resultado |
| --- | --- |
| Línea base y validación | Se reprodujo el fallo del selector de contactos y se corrigió la prueba. Chromium detectó también un salto de línea en la cabecera del chat a 320 px. Se corrigió y se repitió la batería completa. |
| Navegación y herramientas | Mapa por flujo, registro único de suites C#, `--list`, filtros múltiples de Chromium, `--no-build` conectado al test que compila ReleaseTools y ejecución directa de la DLL de pruebas tras resolver su ruta una vez. CI valida el mapa y ejecuta una selección Chromium. |
| Instrucciones para agentes | `AGENTS.md` conserva las reglas de claves y artefactos y añade dos indicaciones de desarrollo. Se verificaron enlaces, nombres de suites y comandos localmente. El ensayo con sesiones independientes se aplazó por el presupuesto elegido por el usuario. |
| WebUI | La proyección de contactos se extrajo del ensamblado. La lista de interfaces se reutiliza en cada snapshot y los contactos se indexan para las búsquedas. El contrato HTTP y WebSocket sigue aceptando a los consumidores existentes. |
| Chat, vídeo y plataformas | Los borradores y el selector de pantalla poseen su estado. QuicWatch tiene una composición compartida que registra protocolos y controla el cierre de sus recursos en escritorio y Android. |
| Núcleo y compilación | El registro de pares posee admisión, índice y expulsión. El builder nativo registra la verificación en `obj/` y copia cada binario solo si cambian sus bytes. La distribución WSL usa una extracción nueva para cada publicación y conserva la caché de compilación nativa. |

[Mapa](development-map.md) · [Plan](plan-mantenimiento-ia.md) · [Ensayo reproducible con presupuesto aparte](../testing/agent-context-pilot.md)

## Rendimiento observado

Tres procesos por variante. Cada proceso calentó diez iteraciones y midió 35 por fase. La tabla usa la mediana de las tres medianas. La última columna mide asignaciones del hilo que proyectó el estado. Cada fila con contactos usa esa cantidad en cada una de las listas de pares disponibles y descubiertos.

| Contactos por lista | Proyección p50 antes → después | Proyección + JSON p50 antes → después | Asignación de proyección antes → después |
| --- | --- | --- | --- |
| 0 | 47,3 → 16,3 ms | 47,3 → 17,4 ms | 331 → 121 kB |
| 50 | 48,4 → 19,8 ms | 48,4 → 20,8 ms | 532 → 294 kB |
| 500 | 94,5 → 18,2 ms | 99,8 → 19,9 ms | 4,13 → 1,88 MB |

Con 500 por lista, el p95 observado para proyección y serialización pasó de 102–137 ms a 24–28 ms. El JSON se mantuvo en cerca de 1,43 MB. El experimento mide una proyección real con datos sintéticos estáticos. Los tiempos en dispositivos con redes o cargas distintas pueden variar. Los alias JSON siguen vigentes y su retirada requeriría migrar consumidores.

En tres repeticiones con los binarios C# ya disponibles, ejecutar tres suites breves pasó de **3,08 s a 0,65 s** (medianas). Se comparó la invocación anterior de `dotnet run --no-build` por suite con la resolución única y ejecución directa de la DLL.

Con la caché de compilación nativa ya disponible, el primer build de Windows que verificó los binarios tardó 26,20 s. El build incremental tardó 0,93 s y omitió la preparación nativa. El hash y el mtime del puente coincidieron antes y después.

```powershell
dotnet run --project QuicPunchTests -c Test -- --benchmark-webui-status --verbose
node scripts/check-development-guide.mjs QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll
npm run test:web
npm run test:browser
```

## Validación de artefactos

- Windows x64 y Linux x64 produjeron ejecutables Native AOT. En sus pruebas aisladas se verificaron los activos embebidos, los scripts, QUIC con PQC, SQLite, identidad y APIs locales.
- Android ARM64 generó el APK de configuración Release para validación local. Se verificó su estructura de firma de pruebas y la presencia de las bibliotecas nativas ARM64. Android x64 generó un APK de prueba y se verificaron el arranque, la WebUI, ambos protocolos QuicWatch y los nuevos scripts en un emulador aislado.
- Las 21 suites JavaScript rápidas y las 57 de Chromium pasaron en local. También pasaron pruebas C# de contrato WebUI y AOT, registro de pares, movilidad WAN, descubrimiento, QuicWatch, seguridad del servidor, recuperación de sesiones y PQC.
- Persisten advertencias de análisis AOT y plataforma de las dependencias y del código existente. La prueba en hardware ARM64 y un flujo de despliegue firmado corresponden a la validación de una publicación externa.
