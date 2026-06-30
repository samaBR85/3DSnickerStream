// swift-tools-version:5.9
import PackageDescription

// The product (the built executable / app) is "3DSnickerStream". The Swift module name
// must be a valid identifier, so the target is named "App" — it isn't user-visible.
let package = Package(
    name: "ThreeDSnickerStream",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "3DSnickerStream", targets: ["App"])
    ],
    targets: [
        .executableTarget(
            name: "App",
            path: "Sources/3DSnickerStream"
        )
    ]
)
