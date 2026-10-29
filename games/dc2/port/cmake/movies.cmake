# The movies' MPEG-2 decoder: a minimal LGPL FFmpeg, the only FFmpeg code the game links.
# ct_port's plain target_link_libraries forbids oc_ffmpeg's keyword form, so the link goes on ct_options,
# the interface ct_link_game hands to the game and to darkchronicle_tests.
include(${OC_ROOT}/cmake/OpenCloudFfmpeg.cmake)
oc_ffmpeg(ct_options INTERFACE)
