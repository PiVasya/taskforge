#pragma once
#include "taskforge_turtle.h"
#include <string>

namespace cturtle {
struct Color {
    int r, g, b;
    Color(int rr = 0, int gg = 0, int bb = 0) : r(rr), g(gg), b(bb) {}
};

class TurtleScreen {
public:
    TurtleScreen(int width = 640, int height = 480) : turtle(width, height) {}
    ~TurtleScreen() { if (!saved) turtle.save("out.ppm"); }
    void bye() { turtle.save("out.ppm"); saved = true; }
    void exitonclick() { bye(); }
    void setup(int, int) {}
    Turtle turtle;
private:
    bool saved = false;
};

class Turtle {
public:
    explicit Turtle(TurtleScreen& screen) : t_(&screen.turtle) {}
    void forward(double v) { t_->forward(v); }
    void backward(double v) { t_->backward(v); }
    void right(double v) { t_->right(v); }
    void left(double v) { t_->left(v); }
    void penup() { t_->penUp(); }
    void pendown() { t_->penDown(); }
    void penUp() { t_->penUp(); }
    void penDown() { t_->penDown(); }
    void width(int v) { t_->penWidth(v); }
    void pensize(int v) { t_->penWidth(v); }
    void pencolor(int r, int g, int b) { t_->penColor(r, g, b); }
    void color(int r, int g, int b) { t_->penColor(r, g, b); }
    void pencolor(Color c) { t_->penColor(c.r, c.g, c.b); }
    void color(Color c) { t_->penColor(c.r, c.g, c.b); }
    void setpos(double x, double y) { t_->setPosition(x, y); }
    void setposition(double x, double y) { t_->setPosition(x, y); }
    void gotoXY(double x, double y) { t_->setPosition(x, y); }
    void circle(double r) { t_->circle(r); }
    void dot(int r = 4) { t_->dot(r); }
private:
    ::Turtle* t_;
};
}
