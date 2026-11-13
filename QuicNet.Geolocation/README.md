# Catálogos locales de ubicación por IP

Esta biblioteca consulta los datos integrados sin conexiones de red, sondeos ni servicios en segundo plano.

- `city.bin`: IPTrust, con país, región y ciudad cuando la fuente la proporciona.
- `asn.bin`: números de red de DB-IP ASN Lite.
- `localities.bin`: nombres y relaciones municipales de GeoNames para completar las etiquetas publicadas.

Las tablas IP utilizan GLP2: diccionarios compartidos, rangos codificados mediante diferencias y páginas comprimidas con Brotli. Se incorporan como recursos comprimidos en la compilación; el lector descomprime páginas bajo demanda con una caché limitada. `Data/manifest.json` conserva fechas, procedencia y hashes. La ciudad publicada es una estimación, no una ubicación física verificada.

Desde la raíz del repositorio:

```powershell
python scripts/prepare-geolocation.py --check
python scripts/prepare-geolocation.py --verify
python scripts/prepare-geolocation.py --iptrust "C:/ruta/iptrust-geolocation.mmdb" --verify
dotnet run --project QuicPunchTests -c Test -- --test-geolocation
```

Las compilaciones normales consumen los recursos ya preparados. Para importar o recomprimir se necesitan las dependencias de `tools/requirements-catalogs.txt`. Las licencias y atribuciones de las fuentes están en `Data/NOTICE.txt`.
