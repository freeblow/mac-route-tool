#!/usr/bin/env swift
import AppKit

// Rebuild the app icon with: swift scripts/generate-icon.swift
let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let resources = root.appendingPathComponent("Resources", isDirectory: true)
let iconset = resources.appendingPathComponent("SRM.iconset", isDirectory: true)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

let size = 1024
guard let bitmap = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: size,
    pixelsHigh: size,
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0
), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
    fatalError("Could not create icon bitmap")
}

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = context
context.imageInterpolation = .high
context.shouldAntialias = true

let tile = NSBezierPath(roundedRect: NSRect(x: 44, y: 44, width: 936, height: 936), xRadius: 210, yRadius: 210)
NSGradient(starting: NSColor(red: 0.10, green: 0.26, blue: 0.44, alpha: 1),
           ending: NSColor(red: 0.035, green: 0.10, blue: 0.22, alpha: 1))!
    .draw(in: tile, angle: -55)

// A single source branches toward two destinations, echoing a routing table.
func stroke(_ points: [NSPoint], width: CGFloat, color: NSColor) {
    let path = NSBezierPath()
    path.lineWidth = width
    path.lineCapStyle = .round
    path.lineJoinStyle = .round
    path.move(to: points[0])
    for point in points.dropFirst() { path.line(to: point) }
    color.setStroke()
    path.stroke()
}

let cyan = NSColor(red: 0.38, green: 0.88, blue: 0.96, alpha: 1)
let mint = NSColor(red: 0.55, green: 0.99, blue: 0.76, alpha: 1)
stroke([NSPoint(x: 252, y: 512), NSPoint(x: 499, y: 512)], width: 76, color: cyan)
stroke([NSPoint(x: 499, y: 512), NSPoint(x: 696, y: 710), NSPoint(x: 774, y: 710)], width: 76, color: cyan)
stroke([NSPoint(x: 499, y: 512), NSPoint(x: 696, y: 314), NSPoint(x: 774, y: 314)], width: 76, color: cyan)

func node(_ point: NSPoint, radius: CGFloat, color: NSColor) {
    color.setFill()
    NSBezierPath(ovalIn: NSRect(x: point.x - radius, y: point.y - radius,
                                width: radius * 2, height: radius * 2)).fill()
}
node(NSPoint(x: 252, y: 512), radius: 74, color: mint)
node(NSPoint(x: 774, y: 710), radius: 74, color: mint)
node(NSPoint(x: 774, y: 314), radius: 74, color: mint)

context.flushGraphics()
NSGraphicsContext.restoreGraphicsState()

guard let png = bitmap.representation(using: .png, properties: [:]) else {
    fatalError("Could not encode icon PNG")
}
let master = resources.appendingPathComponent("SRM.png")
try png.write(to: master)

let variants: [(Int, String)] = [
    (16, "icon_16x16.png"), (32, "icon_16x16@2x.png"),
    (32, "icon_32x32.png"), (64, "icon_32x32@2x.png"),
    (128, "icon_128x128.png"), (256, "icon_128x128@2x.png"),
    (256, "icon_256x256.png"), (512, "icon_256x256@2x.png"),
    (512, "icon_512x512.png"), (1024, "icon_512x512@2x.png")
]
for (pixels, name) in variants {
    let task = Process()
    task.executableURL = URL(fileURLWithPath: "/usr/bin/sips")
    task.arguments = ["-s", "format", "png", "-z", "\(pixels)", "\(pixels)",
                      master.path, "--out", iconset.appendingPathComponent(name).path]
    task.standardOutput = FileHandle.nullDevice
    try task.run()
    task.waitUntilExit()
    guard task.terminationStatus == 0 else { fatalError("sips failed for \(name)") }
}

let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", resources.appendingPathComponent("SRM.icns").path]
try task.run()
task.waitUntilExit()
guard task.terminationStatus == 0 else { fatalError("iconutil failed") }
try FileManager.default.removeItem(at: iconset)
print("Created \(resources.appendingPathComponent("SRM.icns").path)")
