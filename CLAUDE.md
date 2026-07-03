# 3DSnickerStream v2 (Avalonia) — regras do projeto

## O que é
Porte multiplataforma (Windows/macOS/Linux) do 3DSnickerStream para **Avalonia UI 11**, em
`Snickerstream.Avalonia/`. Branch de trabalho: **`avalonia`**. O app WPF em `Snickerstream4Win/` é
**REFERÊNCIA CONGELADA** — nunca editar. O RootNamespace é `SnickerstreamV2` (um namespace terminado
em `.Avalonia` sombrearia o namespace raiz do framework na resolução de nomes do C#).

## Comandos
- Build:   `dotnet build "Snickerstream.Avalonia" -c Release`
- Rodar:   `dotnet run --project "Snickerstream.Avalonia"`
- Publish (teste do usuário, sempre após mudanças):
  `dotnet publish "Snickerstream.Avalonia" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
- Sanidade cross-platform (deve COMPILAR): repetir o publish com `-r linux-x64` e `-r osx-arm64` (`--self-contained true`).
- Antes de publicar: `Get-Process -Name "3DSnickerStream" -EA SilentlyContinue | Stop-Process -Force`.
- Smoke sem 3DS: rodar o exe, `Start-Sleep 5`, checar que o processo continua vivo (a janela abre).

## Regras duras
1. TFM é **net8.0 puro**. PROIBIDO: `net8.0-windows`, `System.Windows.*`, `System.Drawing`, WinRT.
2. Pacotes **Avalonia sempre na mesma versão** 11.x (hoje 11.3.2). Misturar versões = erros de XAML loader.
3. **Não refatorar** `Net/` e `Models/` (portados verbatim do WPF); mudanças ali exigem justificativa no commit.
4. **Um commit por tarefa**: `avalonia(N): resumo` + `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
5. **NUNCA** fazer push sem pedido explícito; **NUNCA** tocar em `windows`/`master`/`macos-apple-silicon`/`gh-pages`.
6. `Avalonia.Media.Imaging.Bitmap` é `IDisposable`: ao trocar `Image.Source`, `Dispose()` no bitmap ANTERIOR;
   nunca no que está em uso. Frames descartados pelo dedup pending/queued também são descartados.
7. Rotação de tela é via **`LayoutTransformControl`** (`RotateTransform`), nunca rotação de pixels.
8. **NÃO escreva `InitializeComponent` à mão** em views com `.axaml`: o gerado pelo Avalonia é quem atribui
   os campos de controles nomeados (`x:Name`). Um `InitializeComponent` manual deixa os campos `null` em runtime.
   Views com ctor parametrizado precisam de um ctor público sem args (designer/loader) — inicialize os campos
   readonly com `= null!` para evitar CS8618.
9. Estilo Avalonia ≠ WPF: sem Triggers — usar seletores de classe + pseudo-classes (`:pointerover`, `:pressed`);
   sobrescrever fundo de botão via `Selector="Button.x /template/ ContentPresenter"`.
10. Slider/valor: observar mudança via `PropertyChanged` + `e.Property == RangeBase.ValueProperty`
    (Rx `GetObservable/Subscribe` não está referenciado).
11. Shell = Windows PowerShell 5.1 (sem `&&`, usar `;`/`if ($?)`) e Git Bash. Caminhos com espaço entre aspas.

## Estado / roadmap
- **Fase 1 (feito):** scaffold, tema, ConnectView essencial, StreamView core (NTR, 2 telas, layouts, FPS, Esc).
- Próximas: gap/escala/zoom, cor por tela (`WriteableBitmap`), clean mode (`SystemDecorations=None`),
  atalhos + janela 2 colunas, screenshots (`RenderTargetBitmap`), find-on-network/auto-connect, presets,
  OCR (Tesseract único p/ 3 SOs), empacotamento (win zip / .app / AppImage) → **v2.0**.

## Verificação padrão de qualquer mudança
build 0 erros → publish win-x64 → reportar caminho do exe → usuário testa com o 3DS real.
