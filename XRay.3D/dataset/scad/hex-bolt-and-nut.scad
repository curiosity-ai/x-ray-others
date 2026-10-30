// Hex head bolt with a modelled metric thread, a hex nut and a washer.
// Written by Innerscene (CC0 1.0). Units: mm. The thread is a 60 degree
// profile swept as a helix with polyhedron().

/* [Bolt] */
// Nominal diameter
d = 12; // [6:1:24]
// Thread pitch
pitch = 1.75; // [0.75:0.25:3]
// Shank length under the head
length = 50; // [20:5:120]
// Threaded length
thread_len = 30; // [10:5:100]

/* [Quality] */
seg = 36;

hex_af = 1.5 * d + 1;
head_h = 0.65 * d;
nut_h = 0.85 * d;
h_t = 0.6134 * pitch; // thread depth
r_maj = d / 2;
r_min = r_maj - h_t;

// One ring of the thread profile at angle a (degrees) and base height z.
function prof(a, z) = [
    [r_min * cos(a), r_min * sin(a), z],
    [r_maj * cos(a), r_maj * sin(a), z + pitch * 0.375],
    [r_maj * cos(a), r_maj * sin(a), z + pitch * 0.5],
    [r_min * cos(a), r_min * sin(a), z + pitch * 0.875]
];

module thread(len) {
    turns = len / pitch;
    n = floor(turns * seg);
    pts = [for (i = [0:n]) each prof(i * 360 / seg, i * pitch / seg)];
    faces = concat(
        [for (i = [0:n - 1], j = [0:2]) [i * 4 + j, i * 4 + j + 1, (i + 1) * 4 + j + 1, (i + 1) * 4 + j]],
        [for (i = [0:n - 1]) [i * 4 + 3, i * 4, (i + 1) * 4, (i + 1) * 4 + 3]],
        [[0, 3, 2, 1]],
        [[n * 4, n * 4 + 1, n * 4 + 2, n * 4 + 3]]
    );
    intersection() {
        union() {
            polyhedron(points = pts, faces = faces);
            cylinder(r = r_min + 0.01, h = len, $fn = seg);
        }
        // Chamfered end
        cylinder(r1 = r_min, r2 = r_maj + 2 * len, h = 2 * len, $fn = seg);
    }
}

module hex(af, h) {
    intersection() {
        cylinder(d = af / cos(30), h = h, $fn = 6);
        // Washer-face chamfer on both ends
        cylinder(d1 = af * 0.95, d2 = af * 0.95 + 4 * h, h = 2 * h, $fn = 48);
        translate([0, 0, -h]) cylinder(d1 = af * 0.95 + 4 * h, d2 = af * 0.95, h = 2 * h, $fn = 48);
    }
}

module bolt() {
    color("silver") {
        translate([0, 0, -head_h]) hex(hex_af, head_h);
        cylinder(d = d - 0.05, h = length - thread_len, $fn = seg);
        translate([0, 0, length - thread_len]) thread(thread_len);
    }
}

module nut() {
    color("lightsteelblue") difference() {
        hex(hex_af, nut_h);
        translate([0, 0, -1]) cylinder(d = d - 2 * h_t * 0.6, h = nut_h + 2, $fn = seg);
    }
}

module washer() {
    color("gainsboro") difference() {
        cylinder(d = 2 * d + 2, h = 0.2 * d, $fn = 64);
        translate([0, 0, -1]) cylinder(d = d + 1, h = d, $fn = seg);
    }
}

bolt();
// Nut and washer laid beside the bolt
translate([2.4 * d, 0, -head_h]) nut();
translate([-2.4 * d, 0, -head_h]) washer();
