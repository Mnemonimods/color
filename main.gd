extends Node

const WORDS := ["SYS_SET_PALETTE_COLOR", "SYS_COLOR_TO_INDEX", "SYS_RESET_PALETTE", "SYS_GET_PALETTE_COLOR", "SYS_SET_PALETTE_RANGE", "SYS_GET_PALETTE_RANGE", "SYS_CLEAR_PALETTE", "SYS_LOAD_PALETTE"]
const COLOR_FROM := "SYS_PRINT_INT"

var _image: Image
var _texture: ImageTexture
var _dirty := false

func _ready():
    get_tree().node_added.connect(_on_node_added)
    _scan(get_tree().root)

    var main = get_tree().root.get_node_or_null("Main")
    if main != null and not main.is_node_ready():
        await main.ready

    await get_tree().process_frame

    var screen = get_tree().root.find_child("Screen", true, false)
    if screen == null or screen.material == null:
        return

    # take the game's own shader and change three lines of it, as to avoid shipping
    # literal code taken from the game with the mod
    var mat = screen.material
    if mat.shader == null:
        return

    var code: String = mat.shader.code
    code = _swap(
        code, 
        "uniform vec3 end_color : source_color;",
        "uniform vec3 end_color : source_color;\nuniform sampler2D palette : source_color, filter_nearest;"
    )
    code = _swap(
        code, 
        "level = max(MIN_LEVEL, level);",
        "float index = floor(level * 255.0 + 0.5);"
    )
    code = _swap(
        code, 
        "vec3 color = mix(start_color, end_color, level);",
        "vec3 color = texture(palette, vec2((index + 0.5) / 256.0, 0.5)).rgb;"
    )

    var shader := Shader.new()
    shader.code = code

    _image = Image.create_empty(256, 1, false, Image.FORMAT_RGB8)
    _fill_default()
    _texture = ImageTexture.create_from_image(_image)

    mat.shader = shader
    mat.set_shader_parameter("palette", _texture)

    add_to_group("misaml_palette")

# replaces one line of the game's shader, warns if the game's shader has changed
func _swap(code: String, old: String, new: String) -> String:
    if not code.contains(old):
        push_warning("colors: shader line not found, game update? " + old)
        return code
    return code.replace(old, new)

# game's own two-color gradient
func _fill_default():
    var dark := Color8(15, 21, 23)
    var pale := Color8(185, 194, 165)
    for i in range(256):
        _image.set_pixel(i, 0, dark.lerp(pale, i / 255.0))

# called from C#, set one palette entry
func set_color(index: int, r: int, g: int, b: int):
    if _image == null or index < 0 or index > 255:
        return
    _image.set_pixel(index, 0, Color8(clampi(r, 0, 255), clampi(g, 0, 255), clampi(b, 0, 255)))
    _dirty = true

# called from C#, replaces the whole palette in one go
# bytes holds 256 entries of R, G, B (768 bytes), index 0 first
func set_palette_bytes(bytes: PackedByteArray):
    if _image == null or bytes.size() != 768:
        return
    _image.set_data(256, 1, false, Image.FORMAT_RGB8, bytes)
    _dirty = true

func reset_palette():
    if _image == null:
        return
    _fill_default()
    _dirty = true

# upload to GPU at most once per frame
func _process(_delta):
    if _dirty and _texture != null:
        _texture.update(_image)
        _dirty = false

# patch editors that already exist
func _scan(node: Node) -> void:
    if node is CodeEdit:
        _patch(node)
    for child in node.get_children():
        _scan(child)

# patch editors created later
func _on_node_added(node: Node) -> void:
    if node is CodeEdit:
        _patch.call_deferred(node)

func _patch(editor: CodeEdit) -> void:
    var highlighter := editor.syntax_highlighter as CodeHighlighter
    if highlighter == null or not highlighter.has_keyword_color(COLOR_FROM):
        return
    var color := highlighter.get_keyword_color(COLOR_FROM)
    for word in WORDS:
        highlighter.add_keyword_color(word, color)