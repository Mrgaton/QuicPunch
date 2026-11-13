# Estado de entrega

Fecha de preparación: 19 de octubre de 2026.

**Se entrega código fuente modificado, no binarios certificados.** Por petición expresa, no se ha compilado el proyecto, no se han ejecutado tests y no se han realizado mediciones ICMP/TCP de validación. Los contadores de supuestas pruebas de conversaciones anteriores no se atribuyen a este ZIP.

## Incluido

Código original con modificaciones e integración predeterminada; motor jerárquico, huellas RTT, topología, diagnóstico de túneles, PMTU IPv4, TCP opt-in, presupuesto común, datos parciales, CLI, replay y GeoJSON. Catálogo semilla con direcciones reales publicadas de RIPE Atlas, recursos binarios/gzip generados y generador de snapshots amplios. Copia de Smart Edit Lite v5 y registro de operaciones aplicado.

## No incluido / no afirmado

- No hay servidor medible por cada municipio ni base de geolocalización IP/ASN/router completa.
- No hay perfiles verdaderos de conexiones españolas, entrenamiento calibrado ni porcentajes de precisión medidos.
- No hay ejecución live de Paris/scamper empaquetada. Existe integración mediante interfaz y lectura de resultados locales recientes.
- No hay medición portable garantizada de PMTU IPv6.
- No hay detección infalible de VPN, especialmente en router/proxy.
- No hay instalador de Windows ni aplicación gráfica heredada: el ZIP original no la contenía. Se añade CLI y visor local de GeoJSON.
- No hay validación de compatibilidad Windows/Linux/macOS, AOT, rendimiento o carga en esta entrega.

## Para una futura liberación de producción

Compilación y pruebas multiplataforma, validación de rutas/PMTU reales, catálogo completo con licencias verificadas, pruebas con redes consentidas de ubicación conocida y análisis de falsos positivos/negativos. Estos pasos son pendientes, no ejecuciones efectuadas.
