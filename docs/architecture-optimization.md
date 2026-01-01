# Architecture en 3 Phases

## Vue d'ensemble

L'agent utilise une architecture optimisee en 3 phases pour minimiser les appels LLM et maximiser la fiabilite.

```
Phase 1 (Prefetch)     Phase 2 (Analyse)     Phase 3 (PR)
     |                       |                    |
App Insights ──┐             │                    │
               ├──> Context ──> Claude ──> Fixes ──> GitHub PR
GitHub Files ──┘             │                    │
     |                       |                    |
  0 appels LLM            1 appel LLM         0 appels LLM
```

## Phase 1 : Prefetching (0 appel LLM)

Recuperation de toutes les donnees necessaires avant d'appeler Claude.

### 1.1 Exceptions depuis App Insights

```csharp
var exceptions = await appInsightsPlugin.GetExceptionsAsync(hours, minOccurrences);
```

### 1.2 Extraction des fichiers sources

```csharp
var sourceFiles = exceptions
    .Where(e => !string.IsNullOrEmpty(e.SourceFile))
    .Select(e => e.SourceFile)
    .Distinct()
    .Take(10);
```

### 1.3 Recuperation parallele depuis GitHub

```csharp
var fileTasks = sourceFiles.Select(async file =>
{
    var json = await _gitHubPlugin.GetFileContentAsync(file);

    // Parse le JSON pour extraire le contenu reel
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("content", out var contentProp))
    {
        return (file, contentProp.GetString() ?? "");
    }
    return (file, "Error: Invalid response");
});

var results = await Task.WhenAll(fileTasks);
```

**Point cle** : `GetFileContentAsync` retourne du JSON, pas du texte brut. Il faut parser pour extraire la propriete `content`.

## Phase 2 : Analyse Claude (1 appel LLM)

### 2.1 Construction du contexte avec numeros de ligne

```csharp
foreach (var (file, content) in fileContents)
{
    contextBuilder.AppendLine($"### {file}");
    contextBuilder.AppendLine("```csharp");

    var lines = content.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        contextBuilder.AppendLine($"{i + 1,4}: {lines[i]}");
    }

    contextBuilder.AppendLine("```");
}
```

**Resultat** : Claude voit le code avec les numeros de ligne, ce qui lui permet de cibler precisement le code a corriger.

```csharp
 267:     public IActionResult SearchUsers([FromQuery] string query)
 268:     {
 269:         var results = _users.Where(u => u.Name.Contains(query)).ToList();
 270:         // BUG: Assumes at least one result exists
 271:         var firstResult = results[0];
 272:         return Ok(firstResult);
 273:     }
```

### 2.2 Prompt structure

```csharp
var prompt = $"""
    You are an expert .NET developer. Analyze these production exceptions and propose fixes.

    CRITICAL: Your `originalCode` must be EXACT text from the source files below.
    Copy the code exactly as shown, including whitespace and comments.

    ## Exceptions
    {exceptionsJson}

    ## Source Files (with line numbers)
    {contextWithLineNumbers}

    Return JSON array of fixes with:
    - filePath: relative path
    - originalCode: EXACT code to replace (copy from above)
    - fixedCode: corrected version
    - explanation: why this fixes the issue
    """;
```

### 2.3 Reponse attendue

```json
{
  "fixes": [
    {
      "filePath": "src/Agent.Api/Controllers/TestExceptionsController.cs",
      "originalCode": "        // BUG: Assumes at least one result exists\n        var firstResult = results[0];\n        logger.LogInformation(\"First match: {Name}\", firstResult.Name);",
      "fixedCode": "        if (results.Count > 0)\n        {\n            var firstResult = results[0];\n            logger.LogInformation(\"First match: {Name}\", firstResult.Name);\n        }",
      "explanation": "Added count check before accessing index 0"
    }
  ]
}
```

## Phase 3 : Creation de PR (0 appel LLM)

### 3.1 Application des fixes par remplacement

```csharp
foreach (var fix in fixes)
{
    // Recuperer le contenu actuel du fichier
    var json = await _gitHubPlugin.GetFileContentAsync(fix.FilePath);
    using var doc = JsonDocument.Parse(json);
    var currentContent = doc.RootElement.GetProperty("content").GetString();

    // Appliquer le fix par remplacement de chaine
    var newContent = currentContent.Replace(fix.OriginalCode, fix.FixedCode);

    if (newContent != currentContent)
    {
        fileChanges.Add(new { Path = fix.FilePath, Content = newContent });
    }
}
```

**Point cle** : La casse des proprietes JSON doit correspondre au `FileChangeDto` dans `GitHubPlugin.cs` (`Path` et `Content` en PascalCase).

### 3.2 Creation de la branche et PR

```csharp
// Creer la branche
var branchName = $"fix/ai-agent-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
await _client.Git.Reference.Create(_owner, _repo, new NewReference(
    $"refs/heads/{branchName}",
    mainRef.Object.Sha));

// Committer les fichiers
foreach (var file in files)
{
    await _client.Repository.Content.UpdateFile(
        _owner, _repo, file.Path,
        new UpdateFileRequest(commitMessage, file.Content, existingSha, branchName));
}

// Creer la PR
var pr = await _client.PullRequest.Create(_owner, _repo, new NewPullRequest(
    title, branchName, _defaultBranch) { Body = prBody });
```

## Mecanisme de fallback

Si `originalCode` contient des placeholders (Claude n'a pas trouve le code exact), utiliser le numero de ligne de l'exception :

```csharp
var isPlaceholder = fix.OriginalCode?.Contains("// Unable") == true ||
    fix.OriginalCode?.Contains("Line 270") == true;

if (isPlaceholder && lineNumber > 0)
{
    // Extraire les lignes autour du numero d'erreur
    var lines = currentContent.Split('\n');
    var startLine = Math.Max(0, lineNumber - 3);
    var endLine = Math.Min(lines.Length - 1, lineNumber + 2);
    var contextLines = string.Join("\n", lines.Skip(startLine).Take(endLine - startLine + 1));

    // Utiliser ce contexte pour le remplacement
}
```

## Comparaison des performances

| Metrique | Avant (sequentiel) | Apres (3 phases) |
|----------|-------------------|------------------|
| Appels Claude | 20-30 | 1 |
| Tokens entree | ~100,000 | ~15,000 |
| Temps total | 2-3 min | 10-20 sec |
| Rate limits | Frequents | Rares |
| Cout estime | ~$3-5 | ~$0.30-0.50 |
| Fiabilite PR | ~30% | ~95% |

## Points cles de l'implementation

1. **Parser le JSON GitHub** : `GetFileContentAsync` retourne `{"path":"...","content":"..."}`, pas du texte brut

2. **Numeros de ligne** : Ajouter les numeros de ligne au contexte pour que Claude puisse cibler precisement

3. **Casse JSON** : Utiliser `Path` et `Content` (PascalCase) pour matcher le `FileChangeDto`

4. **Limite de taille** : Ne pas depasser ~15000 caracteres de contexte pour eviter les troncatures

5. **Fallback** : Detecter les placeholders et utiliser les numeros de ligne comme plan B

## Permissions GitHub requises

Pour un Fine-grained PAT :
- **Contents** : Read and Write (pour creer des branches et committer)
- **Pull requests** : Read and Write (pour creer des PRs)

Sans ces permissions, l'erreur sera : `Resource not accessible by personal access token`
