# LunaOptimizer ⚡

Optimizador de PC para **Windows 10/11** con una interfaz calcada a _Microsoft PC Manager_:
ventana compacta de 456×664, rail lateral con iconos, tarjetas blancas y un único botón
grande para mejorar el rendimiento.

- **Sin instalación**: un `.exe` único y autocontenido (no hace falta tener .NET instalado).
- **Todo en español**, sin telemetría, sin anuncios, sin cuentas.
- Tema **claro u oscuro**; en el primer arranque sigue la preferencia de Windows.
- Motor de limpieza basado en [FluentCleaner](https://github.com/builtbybel/FluentCleaner) (MIT)
  y la base de reglas **Winapp2.ini**.

---

## Novedades de la versión 2.0

| Antes (1.x) | Ahora (2.0) |
| --- | --- |
| Tema oscuro por defecto, rail de 6 pestañas | Paleta y estructura de PC Manager: rail de 8 secciones, tarjetas con borde de 1 px, azul de acento `#0F62B3`, tema claro/oscuro que sigue a Windows |
| Solo Inicio / Procesos / Limpieza / Juegos / Salud / Apps | Se añaden **Protección**, **Almacenamiento**, **Arranque**, **Herramientas** y **Ajustes** |
| — | **Revisión de salud** con puntuación, lista de problemas y acción por línea |
| — | **Antivirus**: estado de Windows Defender, firmas y **examen rápido** real |
| — | **Arranque**: apps que se inician con Windows, activar/desactivar y tiempo de arranque |
| — | **Almacenamiento**: uso por unidad, tamaño de cada carpeta y archivos grandes a la papelera |
| — | **Smart Boost**: optimiza solo cuando la RAM o los temporales pasan del umbral |
| — | **Inicio con Windows** desde Ajustes, con umbrales configurables |

### Rendimiento de la limpieza (2.0.1)

Un TEMP de 30.000 archivos pasó de **~43 s a ~4 s**. Lo que se cambió:

- **Una lectura de metadatos por archivo** (`GetFileAttributesExW`) en lugar de
  `Exists + Attributes + LastWriteTime + Length`, y borrado con `DeleteFileW`.
- **Borrado en paralelo** (4-8 hilos) por lotes, manteniendo la enumeración en un solo hilo.
- **Sin comprobación de existencia** después de borrar en los archivos pequeños
  (solo en los de más de 1 MB, donde la precisión importa).
- La pasada de **carpetas vacías** ya no recorre el árbol entero: solo sube desde las
  carpetas que perdieron archivos. Era el 80 % del tiempo.
- La tarjeta de Inicio **cachea la medida de temporales** 20 s en vez de recorrer el TEMP
  cada vez que entras o cada minuto con Smart Boost.
- La barra de progreso ahora **avanza con los bytes liberados**, no en modo indeterminado.

---

## Descargar

1. Entra en **[Releases](../../releases/latest)** y descarga el `.exe`
   (**`LunaOptimizer-V2.0.1.exe`**).
2. Doble clic → acepta el **UAC** (Windows pide permiso de administrador).
3. Listo: no hay instalador, puedes dejarlo en el Escritorio o en una memoria USB.

**Requisitos:** Windows 10 o 11 (x64). Nada más.

> ⚠️ **El programa pide administrador siempre.** Es necesario para cerrar procesos de otros
> programas, vaciar la caché del sistema, limpiar `%WINDIR%\Temp` y escribir ajustes de
> arranque del equipo. Si dices "No" en el aviso de Windows, la aplicación no arranca.

---

## Cómo se usa

### 🏠 Inicio

![Inicio](docs/inicio.png)

| Zona | Qué hace |
| --- | --- |
| **Banner superior** | Resume la última revisión de salud (puntuación y hace cuánto) y abre Protección. |
| **Mejora de rendimiento** | Libera RAM en varias etapas (working set → lista modificada → standby → libre) y borra temporales de todos los usuarios. Muestra el uso de memoria y los temporales antes de pulsar **Mejorar**. |
| **Smart Boost** | Enlace de la tarjeta: activa o desactiva la optimización automática (se configura en Ajustes). |
| **Cuatro tarjetas** | Revisión de salud, Procesos en curso, Limpieza profunda (uso del disco) y Arranque (tiempo real de arranque de Windows). |
| **Limpieza rápida** | Como un reinicio sin reiniciar (pide confirmación): cierra tus aplicaciones abiertas, borra temporales y DNS y libera RAM. |

**Tema oscuro** (desde Ajustes o el botón de la barra de título):

![Inicio en tema oscuro](docs/inicio-oscuro.png)

### 🛡️ Protección

![Protección](docs/proteccion.png)

**Revisión de salud** en una pasada:

| Comprobación | Qué mira |
| --- | --- |
| Archivos temporales | Tamaño de `%TEMP%` de todos los usuarios |
| Memoria RAM | Porcentaje en uso frente al total |
| Espacio en disco | GB libres y porcentaje del disco del sistema |
| Aplicaciones de arranque | Cuántas se inician con Windows |
| Protección antivirus | Tiempo real y antigüedad de las firmas de Defender |
| Limpieza del sistema | Última limpieza profunda |
| Reinicio pendiente | Actualizaciones de Windows esperando reinicio |

Devuelve una **puntuación de 0 a 100**, una lista con el estado de cada punto y un enlace
para arreglarlo (**Limpiar**, **Revisar**, etc.). La puntuación se guarda y se ve en Inicio.

**Antivirus de Windows**: estado de la protección en tiempo real, última fecha de firmas,
último examen y **Examen rápido** (lanza `MpCmdRun.exe -Scan -ScanType 1`, se puede cancelar).
Si el equipo usa otro antivirus, se avisa y no se toca nada.

### 💾 Almacenamiento

![Almacenamiento](docs/almacenamiento.png)

- **Unidades**: espacio usado y libre con barra, para discos fijos y extraíbles.
- **Carpetas**: mide Descargas, Documentos, Imágenes, Vídeos, Música, Escritorio y Temporales.
- **Archivos grandes**: busca archivos de más de 250 MB y los envía a la **papelera**
  (siempre recuperables; nunca borrado definitivo).
- Acceso directo a **Limpieza profunda**.

### 📋 Procesos

Lista **solo las aplicaciones que es seguro cerrar**: las que tienen una ventana visible,
sin procesos del sistema — el mismo criterio que usa PC Manager.

| Control | Qué hace |
| --- | --- |
| **Buscar** | Filtra por nombre con debounce de 300 ms. |
| **Cerrar** | Cierra ese programa entero (todos sus procesos). Sin confirmación: el resultado va a la barra de estado. |
| **Ubicación** | Abre la carpeta del `.exe` del proceso seleccionado. |
| **Agrupar** | Una fila por programa con todos sus PIDs y su RAM total (activado por defecto). |
| **Auto** | Refresca cada 2 segundos. |
| **Ver todos** | Incluye procesos del sistema y de fondo (aparecen como no cerrables). |

Nunca se pueden cerrar los procesos protegidos (`csrss`, `lsass`, `dwm`, `svchost`,
`explorer`, _Memory Compression_…).

### 🧹 Limpieza

Lista las **aplicaciones instaladas** con el espacio que ocupan sus datos de caché
(reglas de **Winapp2.ini** embebidas: Chrome, Steam, Epic, Spotify, juegos, etc.).

**Limpieza profunda** abre el análisis estilo PC Manager:

![Limpieza profunda](docs/limpieza-profunda.png)

1. **Examinando** → barra de progreso, el elemento actual y los cinco grupos pasando de
   _Esperando a examinar…_ a _Descubierto X_.
2. Al terminar: `Se detectaron X para limpiar` y el botón cambia a **Limpiar**
   (mientras analiza es **Cancelar** y detiene el análisis).
3. **Limpiar** → pide confirmación → borra y muestra lo liberado por grupo.
4. **← Volver** regresa a la pestaña anterior.

**Grupos analizados:** elementos recomendados (`%TEMP%`, `C:\Windows\Temp` y el Temp de cada
usuario), otros del sistema (Windows Update, INetCache, Delivery Optimization, informes WER),
cachés de aplicaciones (Winapp2.ini), seguimientos de uso y papelera de reciclaje.

### ⚡ Arranque

![Arranque](docs/arranque.png)

- Lee las claves `Run` del usuario y del equipo (32 y 64 bits), las carpetas **Inicio** y el
  registro `StartupApproved` que usa el propio Administrador de tareas.
- **Desactivar no borra nada**: escribe el mismo valor binario que escribe Windows (02/03),
  así que se puede volver a activar cuando quieras.
- **Tiempo de arranque**: se lee del evento 100 de _Diagnostics-Performance_ (XML, sin depender
  del idioma del sistema).

### 🗑️ Aplicaciones

Lista las aplicaciones instaladas (registro de 64 y 32 bits, equipo y usuario) con buscador y
**Desinstalar**. Usa el desinstalador de Windows (`msiexec` para MSI; `UninstallString` para el
resto) y borra la entrada del registro si queda huérfana. Los códigos 3010/1641 (pide reiniciar)
se tratan como éxito.

### 🧰 Herramientas

- **Modo juego**: cierra apps, libera RAM y limpia temporales en un clic (con resumen).
- **Rendimiento**: servicios y ajustes de Windows para juegos (SysMain, telemetría, Xbox,
  Game DVR…). Cada fila se **Optimiza** y se **Restaura** al valor exacto original.
- **Arranque** y **Limpieza profunda**, a un clic.
- **Utilidades de Windows**: liberador de espacio, Administrador de tareas, sensor de
  almacenamiento, Windows Update, firewall, restaurar sistema, panel de control e información
  del sistema.

### ⚙️ Ajustes

![Ajustes](docs/ajustes.png)

- **Tema oscuro** (también con el botón de la barra de título).
- **Smart Boost**: activa la optimización automática cuando la RAM pasa del 90 % o los
  temporales de 1 GB (umbrales ajustables con − / +). Nunca se ejecuta si hay vídeo, juego o
  música en curso, ni más de una vez cada 10 minutos.
- **Iniciar con Windows**: alta/baja en `HKCU\…\Run`.
- **Datos y soporte**: carpeta de datos y registro de errores.
- Versión e información del proyecto.

---

## Seguridad y privacidad

- **No hay telemetría, ni red, ni cuentas.** Todo se guarda en
  `%LOCALAPPDATA%\LunaOptimizer\` (`ajustes.json`, `errores.log`, `salud.estado`).
- **SafetyGuard**: barrera de borrado que nunca toca raíces de disco, `System32`, `WinSxS`,
  controladores, carpetas de usuario completas, junctions/symlinks, binarios del sistema ni
  archivos modificados en los últimos 5 minutos.
- **MediaGuard**: si hay vídeo, juego o música en curso no se libera RAM ni se borran cachés
  de GPU, para no cortar la reproducción.
- Los archivos grandes se envían a la **papelera**, nunca se borran de forma definitiva.
- Nada se elimina sin confirmación previa.

---

## Preguntas frecuentes

**¿Cierra mis programas?** La mejora de rendimiento y Smart Boost **no cierran nada**: solo
liberan memoria y borran temporales. Solo se cierra lo que pulses en _Procesos_, _Modo juego_ o
_Limpieza rápida_.

**¿Es seguro?** Sí: no borra nada que no sean temporales, cachés marcadas por Winapp2.ini o la
papelera, y nunca cierra procesos del sistema.

**¿Por qué no aparece el antivirus como protegido?** Si usas otro antivirus, Windows Defender
se desactiva y la revisión lo indica. No se fuerza nada.

**¿No arranca / se cierra solo?** Revisa `%LOCALAPPDATA%\LunaOptimizer\errores.log` (también
desde _Ajustes → Ver registro de errores_) y abre una incidencia en GitHub.

**¿Windows o el navegador dicen que es un troyano?** Es una **falsa positiva**: el `.exe` no
está firmado y SmartScreen avisa a ciegas contra binarios sin reputación. Elige
**Conservar / Permitir en este dispositivo** y, al ejecutarlo, **Más información → Ejecutar de
todas formas**.

**¿Por qué no hay botón de maximizar?** La ventana es fija (456×664), igual que PC Manager.
Sí se puede minimizar.

---

## Compilar desde el código

Necesitas [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/juanmarin19/LunaOptimizer.git
cd LunaOptimizer

# Compilar
dotnet build .\LunaOptimizer\LunaOptimizer.csproj -c Release

# Publicar un .exe único autocontenido (el mismo que sale en Releases)
dotnet publish .\LunaOptimizer\LunaOptimizer.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o .\out
```

Todo el código va dentro del repo (incluidos `FluentCleaner.Core` y `Winapp2.ini`):
**no hay submódulos**, un `git clone` normal ya compila.

### Estructura

```
LunaOptimizer/
├─ App.xaml / App.xaml.cs        recursos globales (tarjetas, botones, interruptores) y arranque
├─ MainWindow.xaml               ventana 456×664: barra de título, rail y host de contenido
├─ Theme/
│  ├─ Light.xaml / Dark.xaml     paletas (mismas claves, se intercambian en caliente)
│  └─ Icons.xaml                 iconos vectoriales (Paths 24×24)
├─ Views/                        Inicio, Protección, Almacenamiento, Procesos, Limpieza,
│                                Limpieza profunda, Arranque, Apps, Herramientas, Ajustes,
│                                Modo juego, Rendimiento
└─ Services/
   ├─ HealthCheckService.cs      revisión de salud con puntuación
   ├─ DefenderService.cs         estado y examen rápido de Defender
   ├─ StartupService.cs          apps de arranque y tiempo de arranque
   ├─ StorageService.cs          unidades, carpetas, archivos grandes, papelera
   ├─ SettingsStore.cs           ajustes en JSON
   ├─ MemoryBoost.cs             liberación de RAM por etapas
   ├─ TempCleaner.cs             temporales, caches, DNS
   ├─ DeepCleanService.cs        Winapp2.ini
   ├─ ProcessService.cs          procesos seguros de cerrar
   ├─ HealthService.cs           servicios y ajustes del sistema
   ├─ SafetyGuard.cs / MediaGuard.cs
   └─ AppUninstallerService.cs
```

---

## Créditos

- **[FluentCleaner](https://github.com/builtbybel/FluentCleaner)** de _builtbybel_ (MIT) —
  motor de detección/limpieza y archivo `Winapp2.ini`.
- **Winapp2.ini** — base de reglas de limpieza mantenida por la comunidad.
- Interfaz inspirada en **Microsoft PC Manager**.

## Licencia

MIT © LunaOptimizer
