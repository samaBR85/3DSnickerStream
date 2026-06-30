#!/usr/bin/env swift
import AppKit
import CoreGraphics

// Renders the SnickerStream app icon: a stylized dual-screen 3DS on a gradient
// "squircle" tile, with a streaming play glyph. Outputs an .iconset + .icns.

func drawIcon(into ctx: CGContext, size S: CGFloat) {
    ctx.setShouldAntialias(true)
    ctx.interpolationQuality = .high

    // Rounded background tile (Big Sur style: small transparent margin).
    let inset = S * 0.075
    let tile = CGRect(x: inset, y: inset, width: S - 2 * inset, height: S - 2 * inset)
    let radius = tile.width * 0.2237
    let tilePath = CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil)

    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    let cs = CGColorSpaceCreateDeviceRGB()
    let grad = CGGradient(colorsSpace: cs, colors: [
        CGColor(red: 0.40, green: 0.22, blue: 0.85, alpha: 1),   // indigo (top)
        CGColor(red: 0.92, green: 0.28, blue: 0.52, alpha: 1)    // pink (bottom)
    ] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(grad, start: CGPoint(x: 0, y: S), end: CGPoint(x: 0, y: 0), options: [])
    ctx.restoreGState()

    // Device body behind the screens (subtle dark rounded rect).
    let bodyW = S * 0.60, bodyH = S * 0.66
    let body = CGRect(x: (S - bodyW) / 2, y: (S - bodyH) / 2, width: bodyW, height: bodyH)
    let bodyR = bodyW * 0.12
    ctx.addPath(CGPath(roundedRect: body, cornerWidth: bodyR, cornerHeight: bodyR, transform: nil))
    ctx.setFillColor(CGColor(red: 0.10, green: 0.10, blue: 0.16, alpha: 0.55))
    ctx.fillPath()

    func roundedScreen(_ r: CGRect, color: CGColor) {
        let rr = CGPath(roundedRect: r, cornerWidth: r.width * 0.06, cornerHeight: r.width * 0.06, transform: nil)
        ctx.addPath(rr)
        ctx.setFillColor(color)
        ctx.fillPath()
    }

    // Top screen (wider) and bottom screen (narrower) — origin is bottom-left.
    let screenColor = CGColor(red: 0.96, green: 0.97, blue: 1.0, alpha: 1)
    let topW = S * 0.46, topH = S * 0.24
    let botW = S * 0.36, botH = S * 0.22
    let gap = S * 0.025
    let topRect = CGRect(x: (S - topW) / 2, y: S / 2 + gap, width: topW, height: topH)
    let botRect = CGRect(x: (S - botW) / 2, y: S / 2 - gap - botH, width: botW, height: botH)
    roundedScreen(topRect, color: screenColor)
    roundedScreen(botRect, color: CGColor(red: 0.78, green: 0.82, blue: 0.92, alpha: 1))

    // Streaming "play" triangle centered on the top screen.
    let t = S * 0.085
    let cx = topRect.midX, cy = topRect.midY
    ctx.beginPath()
    ctx.move(to: CGPoint(x: cx - t * 0.5, y: cy + t * 0.62))
    ctx.addLine(to: CGPoint(x: cx - t * 0.5, y: cy - t * 0.62))
    ctx.addLine(to: CGPoint(x: cx + t * 0.72, y: cy))
    ctx.closePath()
    ctx.setFillColor(CGColor(red: 0.95, green: 0.30, blue: 0.45, alpha: 1))
    ctx.fillPath()

    // Wi-Fi style streaming arcs above the play glyph.
    ctx.setStrokeColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.9))
    ctx.setLineCap(.round)
    let arcCx = botRect.midX, arcCy = botRect.midY
    for (i, rad) in [S * 0.04, S * 0.075, S * 0.11].enumerated() {
        ctx.setLineWidth(S * 0.018)
        ctx.addArc(center: CGPoint(x: arcCx, y: arcCy - S * 0.02),
                   radius: rad, startAngle: .pi * 0.25, endAngle: .pi * 0.75, clockwise: false)
        ctx.setStrokeColor(CGColor(red: 0.30, green: 0.40, blue: 0.95, alpha: Double(1.0 - Double(i) * 0.18)))
        ctx.strokePath()
    }
    ctx.fillEllipse(in: CGRect(x: arcCx - S * 0.012, y: arcCy - S * 0.02 - S * 0.012, width: S * 0.024, height: S * 0.024))
}

func renderPNG(pixels: Int) -> Data {
    let cs = CGColorSpaceCreateDeviceRGB()
    let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8,
                        bytesPerRow: 0, space: cs,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    drawIcon(into: ctx, size: CGFloat(pixels))
    let img = ctx.makeImage()!
    let rep = NSBitmapImageRep(cgImage: img)
    return rep.representation(using: .png, properties: [:])!
}

let iconset = "AppIcon.iconset"
try? FileManager.default.removeItem(atPath: iconset)
try! FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)

let specs: [(Int, Int)] = [(16,1),(16,2),(32,1),(32,2),(128,1),(128,2),(256,1),(256,2),(512,1),(512,2)]
for (base, scale) in specs {
    let px = base * scale
    let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
    try! renderPNG(pixels: px).write(to: URL(fileURLWithPath: "\(iconset)/\(name)"))
}
print("Wrote \(iconset) with \(specs.count) images")
