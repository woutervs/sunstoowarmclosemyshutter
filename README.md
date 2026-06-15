# ShutterAutomation

A .NET 10 Worker Service that automatically controls west-facing shutters based on sun position and weather conditions. Runs as a Docker container and communicates with Shelly Pro Dual Cover/Shutter PM devices over the local network.

## How it works

Every configured interval the service:
1. Calculates the current sun azimuth and elevation from your coordinates (pure math, no API)
2. Fetches current temperature and direct solar radiation from [Open-Meteo](https://open-meteo.com/) (free, no API key required)
3. Evaluates all conditions and decides whether shutters should be closed or open
4. Polls each Shelly device for its current position
5. Only acts if the shutter needs to move — manual positions are respected

### Close conditions (all must be met)
- Temperature ≥ configured threshold
- Sun elevation ≥ minimum (filters out low/grazing sun)
- Sun azimuth within the configured window (sun actually facing your windows)
- Direct solar radiation ≥ threshold (filters out overcast days)

### Open conditions
The service will only reopen a shutter if it was closed by the automation itself (position ≈ closed position ± tolerance). Manual positions are never overridden on open.

### Cooldown
Once the automation closes the shutters, it will not change their state again for the configured cooldown period.

---

## Configuration

Create an `appsettings.json` file in the project root. This file is **not included in the repository** — you must create it yourself.

```json
{
  "Automation": {
    "Latitude": 51.0000,
    "Longitude": 3.0000,
    "TimeZoneId": "Europe/Brussels",
    "CheckIntervalMinutes": 5,
    "CooldownMinutes": 60,

    "Temperature": {
      "CloseThresholdCelsius": 22.0,
      "OpenThresholdCelsius": 20.0
    },

    "Sun": {
      "AzimuthMinDegrees": 210.0,
      "AzimuthMaxDegrees": 300.0,
      "MinElevationDegrees": 10.0
    },

    "Weather": {
      "DirectRadiationThresholdWm2": 150.0
    },

    "Shutter": {
      "ClosedPositionPercent": 20,
      "OpenPositionPercent": 100,
      "PositionTolerancePercent": 5
    },

    "Shutters": [
      { "Name": "Shutter A", "Host": "192.168.x.x", "Channel": 0 },
      { "Name": "Shutter B", "Host": "192.168.x.x", "Channel": 1 },
      { "Name": "Shutter C", "Host": "192.168.x.x", "Channel": 0 }
    ]
  }
}
```

### Settings reference

| Setting | Description |
|---|---|
| `Latitude` / `Longitude` | Your location in decimal degrees. Used for sun position calculation and weather fetch. |
| `TimeZoneId` | IANA timezone identifier e.g. `Europe/Brussels`. Used for local time conversion. |
| `CheckIntervalMinutes` | How often the automation cycle runs. 5 minutes is a sensible default. |
| `CooldownMinutes` | After closing the shutters, how long before the automation is allowed to change state again. |
| `Temperature.CloseThresholdCelsius` | Minimum temperature to trigger closing. |
| `Temperature.OpenThresholdCelsius` | Currently reserved for future hysteresis logic. |
| `Sun.AzimuthMinDegrees` / `AzimuthMaxDegrees` | The azimuth window in which the sun is considered to be facing your windows. For west-facing windows centered around 270°, a range of 210°–300° is typical. Tune to your wall's exact compass bearing. |
| `Sun.MinElevationDegrees` | Minimum sun elevation above the horizon. Filters out early morning/late evening sun that contributes little heat. |
| `Weather.DirectRadiationThresholdWm2` | Minimum direct solar radiation in W/m² to trigger closing. Filters out overcast days where closing the shutters is unnecessary. |
| `Shutter.ClosedPositionPercent` | Target position when closing (0 = fully closed, 100 = fully open). 20 = 80% closed. |
| `Shutter.OpenPositionPercent` | Target position when opening. Typically 100. |
| `Shutter.PositionTolerancePercent` | Tolerance band around target positions to avoid unnecessary motor commands. |
| `Shutters` | List of Shelly shutter channels. Each entry needs a display `Name`, the device `Host` IP, and the `Channel` (0 or 1 for Shelly Pro Dual Cover). |

### Shelly device mapping

The Shelly Pro Dual Cover/Shutter PM controls 2 shutters per device. Map your shutters accordingly:

| Channel | Description |
|---|---|
| 0 | First cover channel (`/roller/0`) |
| 1 | Second cover channel (`/roller/1`) |

---

## Building

```bash
docker build -t shutterautomation:latest .
```

With a specific version tag:

```bash
docker build -t shutterautomation:1.0.0 -t shutterautomation:latest .
```

---

## Running locally

```bash
docker compose up
```

Logs are written to stdout and are accessible via:

```bash
docker logs -f shutter-automation
```

The `appsettings.json` is mounted as a read-only volume so you can update thresholds without rebuilding the image. Changes take effect on the next cycle automatically.

---

## Pushing to a private registry

Tag and push using the included PowerShell script. Copy and adapt `docker-registry-push.ps1`:

```powershell
# Set variables
$registry = "your.registry.host"
$image = "shutterautomation"
$version = "1.0.0"

$pw = Read-Host "Enter Docker registry password" -AsSecureString
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
$PlainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

docker login $registry -u your-username -p $PlainPassword

docker tag ${image}:latest $registry/your-org/${image}:$version
docker tag ${image}:latest $registry/your-org/${image}:latest

docker push $registry/your-org/${image}:$version
docker push $registry/your-org/${image}:latest
```

> **Note:** If your registry is on a private network behind a zero trust solution (e.g. Twingate), make sure the registry resource is added and your client is connected before pushing.

---

## Deploying with Portainer

In Portainer, create a new stack and paste the following compose definition:

```yaml
services:
  shutter-automation:
    image: your.registry.host/your-org/shutterautomation:latest
    container_name: shutter-automation
    restart: unless-stopped
    volumes:
      - /path/to/your/appsettings.json:/app/appsettings.json:ro
    environment:
      - DOTNET_ENVIRONMENT=Production
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__ShutterAutomation=Information
    network_mode: bridge
```

Replace `/path/to/your/appsettings.json` with the absolute path to your `appsettings.json` on the Portainer host, for example `/opt/shutterautomation/appsettings.json`.

> **Important:** Create the `appsettings.json` file on the host **before** deploying the stack. If the file does not exist, Docker will create a directory at that path instead of mounting a file and the container will fail to start.

---

## Network requirements

- The container must be able to reach your Shelly devices over the local network (e.g. VLAN routing configured on your host)
- The container requires outbound internet access to `api.open-meteo.com` for weather data
- No inbound ports are required