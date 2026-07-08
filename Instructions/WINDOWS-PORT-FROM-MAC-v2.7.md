# Portar as melhorias do Mac para Windows/Linux (v2.7+)

Guia para o Claude Desktop (Windows) trazer para o `main` (Windows/Linux) os ajustes feitos
na branch de macOS **sem** puxar nada que seja exclusivo de mac. Leia inteiro antes de mexer.

## Contexto

- **Branch com o trabalho de mac:** `macos-apple-silicon` — commits `avalonia(102)` e `avalonia(103)`.
- **Base compartilhada:** `main` (a partir de `avalonia(101)`), de onde saem os builds de Windows/Linux.
- O mac é um build **exclusivo Apple Silicon/Intel**; parte do que foi feito lá **não compila nem faz
  sentido no Windows**.

Para estudar o diff antes de portar:

```bash
git fetch origin macos-apple-silicon main
git log --oneline origin/main..origin/macos-apple-silicon        # avalonia(102), avalonia(103)
git diff --stat origin/main origin/macos-apple-silicon
git diff origin/main origin/macos-apple-silicon -- <arquivo>     # ver hunk a hunk
```

## Regra de ouro

**Portar só o cross-platform e backend-agnóstico.** NÃO deployar nada que dependa de API da Apple
(MetalFX), de `osascript` (clipboard do mac), ou de comportamento específico do backend **OpenGL** do
mac. Se não compila no Windows ou não roda igual, **não vai**.

---

## ✅ PORTAR (cross-platform — beneficia Windows/Linux)

### 1. Super-xBR de verdade (o maior item)
- **Arquivo novo:** `Snickerstream.Avalonia/Views/SuperXbr.cs` — copiar inteiro. É o algoritmo real do
  Hyllian (2015, MIT), SkSL multipass puro (luma → pass0 → pass1 → pass2 → resample com unsharp).
  Substitui o antigo "Super-xBR" que era só um **stand-in** (xBR-lv2 + AA).
- **Fiação em `Views/GpuScreen.cs`:**
  - `MultiPassFor(...)`: adicionar `UpscaleFilter.SuperXbr => SuperXbr.Passes,`.
  - `IsHeavy(...)`: **remover** `SuperXbr` (deixou de ser single-pass/heavy).
  - Remover `[UpscaleFilter.SuperXbr] = XbrLv2,` do dicionário `Shaders`.
  - Na branch de uniforms single-pass, tirar `SuperXbr` do `if (_filter is Xbr or SuperXbr)` (fica só `Xbr`).
- **Importante:** as duas correções específicas dentro do `SuperXbr.cs` são **seguras em qualquer
  backend** — mantenha as duas:
  1. Superfícies intermediárias tratadas como premultiplied → **alpha sempre 1.0**, luma recalculada do
     RGB por pass (não trafega no alpha).
  2. **`sample()` nunca em fluxo condicional** (dentro de `if`/ternário) — o backend GL retorna preto;
     amostre tudo incondicional e selecione o resultado escalar no fim.
  (No Windows/ANGLE talvez o problema 1/2 nem aparecesse, mas a forma corrigida funciona em todo lugar —
  não "simplifique de volta".)

### 2. Anime4K CNN completo (Upscale 2×→4×) **+ Restore**
- **Arquivos novos:** `Snickerstream.Avalonia/Assets/Anime4K_Restore_CNN_{S,M,L,VL}.glsl` (bloc97, MIT).
- **`Snickerstream.Avalonia.csproj`:** os 4 `<EmbeddedResource Include="Assets\Anime4K_Restore_CNN_*.glsl" />`.
  (Esse é o **único** diff cross-platform do csproj — não há nada de mac nele.)
- **`Views/Anime4KCnn.cs`:** o parser ganhou suporte ao Restore (detecção `Restore`, corpo do regex mais
  tolerante, resíduo `result + MAIN` tratado nos caminhos **dense e offset**, skip do depth-to-space no
  modo Restore). Portar essas mudanças.
- **`Views/GpuScreen.cs`:** `RestoreThenUpscale4x(...)` e o wiring em `MultiPassFor` para
  `Anime4KCnn/M/L/VL`.

### 3. Centralizar a janela da stream ao conectar
- **`MainWindow.axaml.cs`:** método `CenterOnScreen()` + a chamada `Dispatcher.UIThread.Post(CenterOnScreen, …)`
  no fim de `ShowStream`. É Avalonia puro, cross-platform.

### 4. Card **Session** com largura fixa (streambar parava de "dançar")
- **`Views/StreamView.axaml`:** portar **só** `Width="190"` no `ColSession` e `Width="92"` no `FpsBadge`
  (o número de fps mudava de largura e reflowava a streambar inteira).
  **NÃO** portar deste arquivo o item `<ComboBoxItem>MetalFX (Apple)</ComboBoxItem>` (é mac-only — ver abaixo).

### 5. Erro de conexão quebrando em 2 linhas
- **`Views/ConnectView.axaml`:** o status virou um `Grid ColumnDefinitions="Auto,*"` com o `TextBlock`
  usando `TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis"` (antes transbordava por cima
  do "UI Size"/Connect). Cross-platform.

### 6. (Opcional) Renomear os stand-ins enganosos
- No mac, "FSR" virou **"Lanczos"** e "Anime4K" (o antigo) virou **"Lanczos+"** — porque eram Lanczos+sharpen
  disfarçados, não os algoritmos reais. Se quiser a mesma honestidade na UI do Windows, replique os labels
  em `StreamView.axaml` e os comentários no dicionário `Shaders`. **Isso muda a UI do Windows** — decida se
  vale. (Os filtros em si já existem no Windows; é só nome.)

---

## ⛔ NÃO PORTAR (exclusivo de mac — não compila ou não roda no Windows)

- **MetalFX inteiro:**
  - `Snickerstream.Avalonia/Platform/MetalFxUpscaler.cs` (P/Invoke pro dylib) — **pular**.
  - `native/metalfx_helper.m` (Objective-C / MTLFXSpatialScaler) — **pular**.
  - `packaging/macos/make-app.sh` (empacotamento + compile do dylib) — **pular**.
  - `Views/GpuScreen.cs`: `RenderMetalFx(...)`, a propriedade `MfxSharpen`, e a branch
    `if (_filter == UpscaleFilter.MetalFx …)` no início de `Render` — **pular**.
  - `Imaging/Upscaler.cs`: o único diff é adicionar `MetalFx` no fim do enum — **pular** (ou deixar o valor
    unused, mas então **não** adicione o item no combo).
  - MTLFX é API da Apple; nem linka no Windows.
- **Clipboard do mac:**
  - `Snickerstream.Avalonia/Platform/MacClipboard.cs` (usa `osascript`/PNGf) — **pular**.
  - `Views/StreamView.axaml.cs`: a branch `if (OperatingSystem.IsMacOS()) return MacClipboard…` e a lógica
    de desabilitar o item MetalFX — **pular**. O Windows já tem o caminho CF_DIB funcionando.
- **Correção de cor (R↔B / BGRA) — a mais perigosa:**
  - Em `GpuScreen.SetFrame` o mac normaliza todo frame pra BGRA (`SwapRedBlue` quando `Rgba8888`) e
    **removeu** o `RbSwap`. Isso é específico do **backend GL do mac** (o JPEG decodifica pra `Rgba8888`
    no GL; no Windows/ANGLE o nativo já é BGRA). **As cores no Windows já estão corretas — NÃO porte essa
    parte**, senão inverte R↔B no Windows. Se você copiar `GpuScreen.cs` inteiro por engano, isso vem junto:
    porte **cirurgicamente**.
- `.gitignore` (ignores de `dist/` e `native/*.dylib`) — inócuo; opcional, tanto faz.

---

## ⚠️ Arquivos MISTOS (cross-platform + mac no mesmo arquivo) — porte hunk a hunk

| Arquivo | Portar | NÃO portar |
|---|---|---|
| `Views/GpuScreen.cs` | wiring Super-xBR, `RestoreThenUpscale4x` | `RenderMetalFx`, `MfxSharpen`, branch MetalFx, mudança de cor em `SetFrame` |
| `Views/StreamView.axaml` | `Width` do `ColSession`/`FpsBadge` | item `MetalFX (Apple)` no combo |
| `Views/StreamView.axaml.cs` | (nada) | branch MacClipboard + disable MetalFX |
| `Imaging/Upscaler.cs` | (nada) | valor `MetalFx` no enum |
| `Snickerstream.Avalonia.csproj` | os 4 `EmbeddedResource` do Restore | (não há bits de mac aqui) |

**Não copie `GpuScreen.cs` inteiro.** Ele tem os três tipos misturados (cross-platform, mac-only e
mac-GL-cor). Traga só os hunks do Super-xBR e do Anime4K Restore.

---

## Verificação no Windows (antes de commitar/deployar)

1. **Compila** sem os símbolos de mac — garanta que nada não-guardado referencia `MetalFxUpscaler` ou
   `MacClipboard` (eles não existem no Windows).
2. **Super-xBR** aparece e funciona (não fica preto), com diagonais suavizadas.
3. **Cores corretas em todos os modos de compressão** (Uncompressed/UDP, JPEG/Reliable, Lossless/Delta).
   Se o R↔B inverter, você trouxe a mudança de cor do `SetFrame` por engano — reverta essa parte.
4. **Anime4K CNN S/M/L/VL** (com Restore) não fica preto e não mostra só highlights.
5. **Card Session** não muda de largura quando o fps varia; **erro de conexão** quebra em 2 linhas.
6. Nada de item "MetalFX (Apple)" no combo de upscale do Windows.

## Não esquecer

- **Não commitar/pushar sem pedido explícito** do usuário. Trabalhe em branch, não direto no `main`.
- Nomear/versionar conforme a convenção do projeto (`avalonia(N): …`, `Co-Authored-By`).
- O release de Windows/Linux é feito pelo CI do projeto; **não** subir manualmente os assets de mac.
