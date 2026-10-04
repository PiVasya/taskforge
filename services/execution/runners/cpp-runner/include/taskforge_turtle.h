#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

class Turtle {
public:
    Turtle(int width = 640, int height = 480)
        : w_(std::max(1, width)), h_(std::max(1, height)), pixels_(static_cast<size_t>(w_) * h_ * 3, 255) {
        x_ = w_ / 2.0;
        y_ = h_ / 2.0;
        heading_ = 0.0;
    }

    void forward(double distance) { move(distance); }
    void backward(double distance) { move(-distance); }
    void right(double degrees) { heading_ -= degrees; }
    void left(double degrees) { heading_ += degrees; }
    void setHeading(double degrees) { heading_ = degrees; }
    void heading(double degrees) { setHeading(degrees); }

    void penUp() { down_ = false; }
    void penup() { penUp(); }
    void pu() { penUp(); }
    void penDown() { down_ = true; }
    void pendown() { penDown(); }
    void pd() { penDown(); }

    void penWidth(int width) { penWidth_ = std::max(1, width); }
    void width(int width) { penWidth(width); }

    void penColor(int r, int g, int b) { r_ = clamp255(r); g_ = clamp255(g); b_ = clamp255(b); }
    void pencolor(int r, int g, int b) { penColor(r, g, b); }
    void color(int r, int g, int b) { penColor(r, g, b); }

    void setPosition(double x, double y) { x_ = x; y_ = y; }
    void setposition(double x, double y) { setPosition(x, y); }
    void gotoXY(double x, double y) { setPosition(x, y); }
    void goTo(double x, double y) { setPosition(x, y); }

    void dot(int radius = 4) {
        int rr = std::max(1, radius);
        int cx = static_cast<int>(std::round(x_));
        int cy = static_cast<int>(std::round(y_));
        for (int yy = cy - rr; yy <= cy + rr; ++yy) {
            for (int xx = cx - rr; xx <= cx + rr; ++xx) {
                int dx = xx - cx, dy = yy - cy;
                if (dx * dx + dy * dy <= rr * rr) setPixel(xx, yy, r_, g_, b_);
            }
        }
    }

    void circle(double radius) {
        const int steps = std::max(24, static_cast<int>(std::abs(radius) * 0.7));
        const double startX = x_;
        const double startY = y_;
        const double startHeading = heading_;
        const double step = 360.0 / steps;
        const double len = 2.0 * 3.14159265358979323846 * radius / steps;
        for (int i = 0; i < steps; ++i) {
            forward(len);
            left(step);
        }
        heading_ = startHeading;
        x_ = startX;
        y_ = startY;
    }

    void clear() { std::fill(pixels_.begin(), pixels_.end(), 255); }
    void reset() { clear(); x_ = w_ / 2.0; y_ = h_ / 2.0; heading_ = 0.0; down_ = true; }

    void save(const std::string& path = "out.ppm") const {
        std::ofstream f(path, std::ios::binary);
        f << "P6\n" << w_ << " " << h_ << "\n255\n";
        f.write(reinterpret_cast<const char*>(pixels_.data()), static_cast<std::streamsize>(pixels_.size()));
    }

private:
    int w_, h_;
    std::vector<unsigned char> pixels_;
    double x_ = 0, y_ = 0, heading_ = 0;
    bool down_ = true;
    int penWidth_ = 2;
    unsigned char r_ = 0, g_ = 0, b_ = 0;

    static unsigned char clamp255(int v) { return static_cast<unsigned char>(std::clamp(v, 0, 255)); }

    void move(double distance) {
        double rad = heading_ * 3.14159265358979323846 / 180.0;
        double nx = x_ + std::cos(rad) * distance;
        double ny = y_ - std::sin(rad) * distance;
        if (down_) drawLine(x_, y_, nx, ny);
        x_ = nx;
        y_ = ny;
    }

    void setPixel(int x, int y, unsigned char r, unsigned char g, unsigned char b) {
        if (x < 0 || y < 0 || x >= w_ || y >= h_) return;
        size_t i = (static_cast<size_t>(y) * w_ + x) * 3;
        pixels_[i] = r; pixels_[i + 1] = g; pixels_[i + 2] = b;
    }

    void drawThickPoint(int x, int y) {
        int rad = std::max(0, penWidth_ / 2);
        for (int yy = y - rad; yy <= y + rad; ++yy)
            for (int xx = x - rad; xx <= x + rad; ++xx)
                setPixel(xx, yy, r_, g_, b_);
    }

    void drawLine(double x0d, double y0d, double x1d, double y1d) {
        int x0 = static_cast<int>(std::round(x0d));
        int y0 = static_cast<int>(std::round(y0d));
        int x1 = static_cast<int>(std::round(x1d));
        int y1 = static_cast<int>(std::round(y1d));
        int dx = std::abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -std::abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true) {
            drawThickPoint(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
};
