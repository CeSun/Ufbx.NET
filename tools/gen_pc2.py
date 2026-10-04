import struct, math, os
# POINTCACHE2 header: 11-byte magic + 1 pad + u32 version + u32 num_points
# + f32 start_frame + f32 frames_per_sample + u32 num_samples
def write_pc2(path, num_points, num_samples, start_frame, fps, data_fn):
    header = b"POINTCACHE2" + b"\x00"  # 12 bytes (11-byte magic + pad)
    header += struct.pack("<I", 1)          # version @12
    header += struct.pack("<I", num_points) # num_points @16
    header += struct.pack("<f", start_frame)# start_frame @20
    header += struct.pack("<f", fps)        # frames_per_sample @24
    header += struct.pack("<I", num_samples)# num_samples @28
    assert len(header) == 32, len(header)
    body = bytearray()
    for s in range(num_samples):
        for p in range(num_points):
            body += struct.pack("<fff", *data_fn(s, p))
    with open(path, "wb") as f:
        f.write(header + bytes(body))

os.makedirs("tools/s4a_inputs", exist_ok=True)
write_pc2("tools/s4a_inputs/synth_a.pc2", 4, 3, 0.0, 1.0,
          lambda s, p: (s + p*0.5, math.sin(s*0.3 + p), p - s*0.25))
write_pc2("tools/s4a_inputs/synth_b.pc2", 3, 5, 2.0, 0.5,
          lambda s, p: (p*1.5, -s*0.75 + p, 2.0 + s*0.125))
print("wrote pc2 samples")
