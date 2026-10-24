# Tidy

Optimizador seguro para Windows 11: limpieza, optimizaciones, drivers, apps, red, seguridad y modo juego, en español.

**Descarga:** la última versión está en [Releases](../../releases/latest).

## Qué hace

- **Limpieza y almacenamiento:** temporales, cachés, archivos grandes, descargas y duplicados. Antes de borrar te enseña la lista exacta de archivos; lo de Almacenamiento va a la Papelera.
- **Optimizaciones:** más de 80, con perfiles (Recomendado, Gaming, Privacidad, Rendimiento máximo). Cada una guarda el valor original y se deshace con su interruptor.
- **Aplicaciones:** desinstalador en profundidad (los restos van a una cuarentena recuperable), procesos, y actualizar o instalar apps con winget.
- **Rendimiento:** liberación automática de RAM, planes de energía y modo juego automático.
- **Drivers:** desde Windows Update y nvidia.com (se comprueba la firma digital).
- **Red, servicios, tareas e inicio de Windows**, con copia de seguridad de todo.
- **Seguridad:** revisión de solo lectura (antivirus, firewall, UAC, Secure Boot, TPM, discos…).
- **Historial:** todo lo que hace Tidy, con botón para deshacer, y comparación antes/después.

## Lo que Tidy no hace a propósito

No desactiva Defender, el firewall, el UAC, SmartScreen, Windows Update ni las protecciones contra Spectre/Meltdown. Ninguna optimización baja la seguridad del PC.

## Requisitos

Windows 11 (64 bits) y el [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Compilar

```
dotnet publish src/Tidy -c Release -p:PublishSingleFile=true -p:DebugType=none -o dist
```

Pruebas de seguridad (se ejecutan en una carpeta aislada):

```
dotnet run --project tests/Tidy.Tests
```
