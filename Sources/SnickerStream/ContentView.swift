import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: StreamViewModel

    var body: some View {
        Group {
            if model.isStreaming {
                StreamView()
            } else {
                ConnectView()
            }
        }
    }
}
