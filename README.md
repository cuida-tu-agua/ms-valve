# ms-valve (valve-service · :3005)

Electroválvula de cada lugar: estado confirmado (HU-019), cerrar con código al correo (HU-020), abrir (HU-021), historial (HU-022).
.NET 10 · hexagonal · SQL Server (schema `valve`, lo crea **ms-valve-db**) · RabbitMQ.

## Flujo de una orden
App → `POST /valve/close {code}` → ms-iam verifica el código → orden `SENT` (30 s) → ms-devices (`X-Internal-Key`) → MQTT `valve/set` → ESP32 →
`valve/state` con el mismo `commandId` → ms-devices → RabbitMQ `device.valve.reported` → aquí: `ACK_SUCCESS`.
Sin respuesta en 30 s → `ACK_TIMEOUT` (barrido cada 5 s). El estado mostrado es siempre el que **confirmó el equipo**.

## Endpoints (JWT)
| Método | Ruta | |
|---|---|---|
| GET | `/api/places/{id}/valve` | estado, comunicación, orden en curso |
| POST | `/api/places/{id}/valve/close` `{code}` | 202 |
| POST | `/api/places/{id}/valve/open` | 202 |
| GET | `/api/places/{id}/valve/commands/{commandId}` | progreso de una orden |
| GET | `/api/places/{id}/valve/commands?from&to` | historial (30 días por defecto) |

## Puesta en marcha
```powershell
dotnet user-secrets set "ConnectionStrings:Valve" "Server=localhost,1433;Database=sy-water-db;User Id=valve_app;Password=<...>;TrustServerCertificate=True" --project src/SyWater.Valve.Api
dotnet user-secrets set "RabbitMq:Password" "<RABBITMQ_PASSWORD>" --project src/SyWater.Valve.Api
dotnet user-secrets set "Internal:ApiKey" "<la MISMA que en ms-devices>" --project src/SyWater.Valve.Api
Copy-Item ..\ms-iam\keys\public.pem src\SyWater.Valve.Api\keys\iam-public.pem -Force
dotnet test
dotnet run --project src/SyWater.Valve.Api
```
