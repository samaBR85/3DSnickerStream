# Melhorias 3DSnickerStream (Windows) — aplicar v1.0 → v1.0.8

Instruções para o Claude Code aplicar, no projeto **3DSnickerStream** (branch **`windows`**, app C# WPF `Snickerstream4Win`, `net8.0-windows`), as 3 melhorias validadas + o número de versão. São mudanças pequenas e cirúrgicas; **não** refatore nada além do descrito.

## O que essas melhorias fazem (resumo)
1. **Buffer UDP grande + SO_REUSEADDR** no cliente NTR → menos frames perdidos em rajada de WiFi e reconexão sem falha de bind.
2. **Destravar o teto de FPS** (default de 30 → ∞) → o app deixa de descartar frames que o 3DS já manda (corrige o sintoma "received > rendered").
3. **Versão 1.0.8** (marcador visível no menu *About*).

---

## Mudança 1 — Buffer UDP + REUSEADDR
**Arquivo:** `Snickerstream4Win/Net/NTRClient.cs`, dentro do método `Start()`.
Os `using System.Net;` e `using System.Net.Sockets;` já existem no topo do arquivo — não precisa adicionar.

**Localizar:**
```csharp
        try
        {
            _udp = new UdpClient(_listenPort);
        }
```

**Substituir por:**
```csharp
        try
        {
            // Bind com SO_REUSEADDR: reconexão rápida não falha se o SO ainda não
            // liberou o socket anterior na mesma porta.
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // Buffer de recepção grande: o NTR manda os pedaços do JPEG em rajada por UDP;
            // um buffer pequeno do SO derruba pacotes na rajada, e um pedaço perdido descarta
            // o frame inteiro, baixando o FPS efetivo. Alguns MB absorvem a rajada.
            try { udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { /* best effort */ }
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            _udp = udp;
        }
```
(O bloco `catch (Exception ex) { ... }` logo abaixo permanece igual.)

---

## Mudança 2 — Destravar o teto de FPS
**Arquivo:** `Snickerstream4Win/Models/AppSettings.cs`.

**Localizar:**
```csharp
    public int MaxFps { get; set; } = 30;                 // 0 = unlimited
```

**Substituir por:**
```csharp
    public int MaxFps { get; set; } = 0;                  // 0 = unlimited (mostra o FPS real do console)
```

> Observação: isso muda só o **default** (instalações novas). Se já existir um `settings.json` em `%APPDATA%\3DSnickerStream\`, ele mantém o valor salvo — nesse caso, ponha **Max FPS = ∞** na interface, ou apague o `settings.json` para regenerar com o novo default.

---

## Mudança 3 — Versão no About
**Arquivo:** `Snickerstream4Win/Models/QualityPreset.cs` (classe `AppInfo`).

**Localizar:**
```csharp
    public const string Version = "1.0";
```
**Substituir por:**
```csharp
    public const string Version = "1.0.8";
```

## Mudança 4 — Versão do projeto
**Arquivo:** `Snickerstream4Win/Snickerstream4Win.csproj`.

**Localizar:**
```xml
    <Version>1.0.0</Version>
```
**Substituir por:**
```xml
    <Version>1.0.8</Version>
```

---

## Build e verificação
```powershell
cd Snickerstream4Win
dotnet build -c Release
```
Deve compilar com **0 erros**. Rodar o `.exe` gerado em `bin/Release/net8.0-windows/win-x64/3DSnickerStream.exe` e confirmar, no menu **ⓘ About**, que aparece **Version 1.0.8**.

---

> **Nota:** aplicar **somente** as 4 mudanças acima. Não refatorar a rotação nem adicionar lógica de "listen-first" na reconexão — as duas foram testadas e revertidas aqui (causavam crash ~3s e sliders sem efeito, respectivamente).
