// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "SnickerStream",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        .executableTarget(
            name: "SnickerStream",
            path: "Sources/SnickerStream"
        )
    ]
)
