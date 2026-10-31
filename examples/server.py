"""可在第二栏阅读的后端代码。轻页不会自动运行它。"""
from http.server import SimpleHTTPRequestHandler, HTTPServer


class PreviewHandler(SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()


if __name__ == "__main__":
    server = HTTPServer(("127.0.0.1", 8000), PreviewHandler)
    print("本机服务：http://localhost:8000")
    server.serve_forever()
