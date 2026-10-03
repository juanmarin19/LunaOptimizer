# LunaOptimizer ⚡

Optimizador de PC para **Windows 10/11** con interfaz estilo *Microsoft PC Manager*.
Una sola ventana de 456×664 con tres pestañas: **Boost**, **Procesos** y **Limpieza**.

- **Sin instalación**: es un `.exe` único y autocontenido (no hace falta tener .NET instalado).
- **Todo en español**, sin telemetría, sin anuncios, sin suscripciones.
- Motor de limpieza basado en [FluentCleaner](https://github.com/builtbybel/FluentCleaner) (MIT) y la base de reglas **Winapp2.ini**.

---

## Descargar

1. Entra en **[Releases](https://github.com/juanmarin19/LunaOptimizer/releases)** y descarga **`LunaOptimizer-v4.exe`**.
2. Doble clic → acepta el **UAC** (Windows pregunta por permiso de administrador).
3. Ya está: no hay instalador, puedes dejarlo en el Escritorio o en una memoria USB.

**Requisitos:** Windows 10 o 11 (x64). Nada más.

> ⚠️ **El programa pide administrador siempre.** Es necesario para cerrar procesos de
> otros programas, vaciar la caché del sistema y limpiar `%WINDIR%\Temp`. Si le dices
> "No" en el aviso de Windows, la aplicación no arranca.

---

## Cómo se usa

### ⚡ Boost
| Botón | Qué hace | Cuándo usarlo |
|---|---|---|
| **Optimizar RAM ahora** | Vacía la *working set* de todos los procesos y fuerza el GC. **No cierra ninguna app.** | Cuando la PC va lenta y tienes muchas cosas abiertas. |
| **Limpiar temporales** | Borra archivos temporales (`%TEMP%`, `C:\Windows\Temp`, *Delivery Optimization*, *INetCache*) y hace `ipconfig /flushdns`. | De vez en cuando, o antes de jugar. |

El cuadro de texto de abajo muestra el resultado: procesos optimizados, RAM
disponible antes/después y cuánto se ganó.

### 📋 Procesos
Lista todos los procesos ordenados por consumo de RAM, con icono, PID, consumo y estado.

| Botón | Qué hace |
|---|---|
| **Actualizar** | Vuelve a leer la lista. |
| **Terminar** | Cierra los procesos seleccionados (y sus hijos). Hay una confirmación previa. |
| **Ubicación** | Abre la carpeta donde está el `.exe` del proceso seleccionado. |

- Escribe en el cuadro de **búsqueda** para filtrar por nombre.
- **No se pueden cerrar** los procesos del sistema (`csrss`, `lsass`, `dwm`,
  `svchost`, `explorer`, *Memory Compression*…): están en una lista protegida.
- Si un programa "no responde", selecciónalo y pulsa **Terminar**.

### 🧹 Limpieza
Limpieza profunda de aplicaciones instaladas usando las reglas de **Winapp2.ini**
(miles de reglas de Chrome, Steam, Epic, Spotify, juegos, etc.).

1. **Analizar** → mide el espacio que ocupa cada aplicación. (Al abrir la pestaña
   se analiza solo.)
2. Marca las que quieras en la lista.
3. **Limpiar** → confirma y borra.

> La base de reglas viaja **dentro del propio `.exe`**, así que funciona en
> cualquier PC sin archivos sueltos.

---

## Preguntas frecuentes

**¿Es seguro?** Sí: no borra nada que no sean temporales o datos de caché marcados
por Winapp2.ini, y nunca cierra procesos del sistema. Se recomienda igualmente
cerrar el programa después de usarlo.

**¿Cierra mis programas?** No. La *Boost* solo libera memoria. Solo se cierra lo que
tú marques y confirmes en la pestaña *Procesos*.

**¿No arranca / se cierra solo?** Revisa el log de errores en
`%LOCALAPPDATA%\LunaOptimizer\errores.log` y ábrelo como incidencia en GitHub.

**¿Por qué no hay icono de maximizar?** Porque la ventana es fija (456×664), al
igual que Microsoft PC Manager. Sí se puede minimizar.

---

## Compilar desde el código

Necesitas [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone --recurse-submodules https://github.com/juanmarin19/LunaOptimizer.git
cd LunaOptimizer

# Compilar
dotnet build .\LunaOptimizer\LunaOptimizer.csproj -c Release

# Publicar un .exe único autocontenido (el mismo que sale en Releases)
dotnet publish .\LunaOptimizer\LunaOptimizer.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o .\out
```

> El `FluentCleaner` que está a la par es un **submódulo**: si clonas sin
> `--recurse-submodules` el proyecto no compila.

---

## Créditos

- **[FluentCleaner](https://github.com/builtbybel/FluentCleaner)** de *builtbybel* (MIT) —
  motor de detección/limpieza y archivo `Winapp2.ini`.
- **Winapp2.ini** — base de reglas de limpieza mantenida por la comunidad.
- Interfaz inspirada en **Microsoft PC Manager**.

## Licencia

MIT © LunaOptimizer
