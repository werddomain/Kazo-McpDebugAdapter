# McpDebugAdapter.WpfHelper

Une librairie WPF pour améliorer la découverte et l'interaction avec les contrôles lors du débogage avec MCP Debug Adapter.

## Installation

1. Construire la librairie :
```bash
cd src/McpDebugAdapter.WpfHelper
dotnet build -c Release
```

2. Dans votre application WPF, ajouter la référence au projet ou package NuGet.

## Usage Simple

### Dans votre App.xaml.cs

```csharp
using McpDebugAdapter.WpfHelper;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Initialiser le support de débogage WPF
        #if DEBUG
        await WpfDebugInitializer.InitializeAsync(port: 8899);
        #endif
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        #if DEBUG
        await WpfDebugInitializer.ShutdownAsync();
        #endif
        
        base.OnExit(e);
    }
}
```

## Fonctionnalités

### API HTTP (Port 8899 par défaut)

- **GET /controls** - Obtenir tous les contrôles
- **GET /controls/by-name?name=ButtonName** - Trouver par nom
- **GET /controls/by-type?type=Button** - Trouver par type
- **GET /controls/by-text?text=Click** - Trouver par texte
- **GET /controls/by-automation-id?automationId=SaveButton** - Trouver par AutomationId
- **GET /controls/at-point?x=100&y=200** - Contrôle à une position
- **POST /highlight?name=ButtonName&highlight=true** - Surligner un contrôle
- **POST /refresh** - Actualiser la cache des contrôles

### Exemple de réponse JSON

```json
{
  "controls": [
    {
      "name": "SaveButton",
      "type": "Button",
      "text": "Save",
      "bounds": {
        "x": 100,
        "y": 200,
        "width": 80,
        "height": 30
      },
      "center": {
        "x": 140,
        "y": 215
      },
      "isVisible": true,
      "isEnabled": true,
      "automationId": "SaveBtn",
      "zIndex": 0,
      "toolTip": "Save the document"
    }
  ]
}
```

## Usage Avancé

### Utilisation directe de l'API

```csharp
var debugHelper = new WpfDebugHelper(Application.Current);

// Trouver tous les boutons
var buttons = debugHelper.FindControlsByType("Button");

// Trouver un contrôle par nom
var saveButton = debugHelper.FindControlsByName("SaveButton").FirstOrDefault();

// Obtenir le contrôle à une position
var controlAtPoint = debugHelper.GetControlAt(new Point(100, 200));

// Surligner un contrôle pour le débogage
debugHelper.HighlightControl("SaveButton", true);
```

### Service HTTP personnalisé

```csharp
var debugHelper = new WpfDebugHelper(Application.Current);
var httpService = new WpfDebugHttpService(debugHelper, port: 9000);

await httpService.StartAsync();
// ... votre application
await httpService.StopAsync();
```

## Avantages par rapport à l'automation Windows

1. **Précision** : Accès direct aux objets WPF, pas de problèmes de timing
2. **Performance** : Pas besoin d'interroger l'arbre UI Windows
3. **Richesse des données** : Accès aux propriétés internes WPF
4. **Coordonnées exactes** : Conversion automatique client → écran
5. **Débogage visuel** : Capacité de surligner les contrôles
6. **Temps réel** : API HTTP pour intégration avec outils externes

## Intégration avec MCP Debug Adapter

Une fois la librairie initialisée dans votre application WPF, le MCP Debug Adapter peut :

1. Interroger l'API HTTP pour obtenir des informations précises sur les contrôles
2. Utiliser les coordonnées `center` pour des clics précis
3. Utiliser les noms/types/automation IDs pour une sélection fiable
4. Surligner les contrôles pour vérification visuelle

## Exemple d'utilisation avec curl

```bash
# Obtenir tous les contrôles
curl http://localhost:8899/controls

# Trouver un bouton spécifique
curl "http://localhost:8899/controls/by-name?name=SaveButton"

# Surligner un contrôle
curl -X POST "http://localhost:8899/highlight?name=SaveButton&highlight=true"
```