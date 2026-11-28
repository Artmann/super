#version 460

// Fills two targets with a colour each, given as push constants. The GPU layer's tests draw with it.
layout(push_constant) uniform Constants {
    vec4 first;
    vec4 second;
} constants;

layout(location = 0) out vec4 outFirst;
layout(location = 1) out vec4 outSecond;

void main() {
    outFirst = constants.first;
    outSecond = constants.second;
}
