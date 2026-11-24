import base64
import contextlib
import json
import sys
import traceback


def main():
    config = json.loads(sys.argv[1])
    with contextlib.redirect_stdout(sys.stderr):
        from paddleocr import PaddleOCR
        import cv2
        import numpy as np

    engines = {}

    def get_engine(language):
        if language not in engines:
            engines[language] = PaddleOCR(
                lang=language,
                text_detection_model_dir=config["det_model_dir"],
                text_recognition_model_dir=config["rec_model_dir"],
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            )
        return engines[language]

    print("READY", flush=True)
    for request_line in sys.stdin:
        try:
            request = json.loads(request_line)
            image_bytes = np.frombuffer(base64.b64decode(request["image"]), dtype=np.uint8)
            image = cv2.imdecode(image_bytes, cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError("Invalid image data")
            with contextlib.redirect_stdout(sys.stderr):
                result = get_engine(request["language"]).predict(image)
            lines = []
            for page in result:
                texts = page.get("rec_texts", [])
                scores = page.get("rec_scores", [])
                polygons = page.get("rec_polys", [])
                if len(polygons) != len(texts):
                    polygons = page.get("rec_boxes", [])
                for text, score, polygon in zip(texts, scores, polygons):
                    points = np.asarray(polygon).reshape(-1, 2)
                    left = int(np.floor(points[:, 0].min()))
                    top = int(np.floor(points[:, 1].min()))
                    right = int(np.ceil(points[:, 0].max()))
                    bottom = int(np.ceil(points[:, 1].max()))
                    if right > left and bottom > top:
                        lines.append({
                            "text": str(text),
                            "confidence": float(score),
                            "x": left,
                            "y": top,
                            "width": right - left,
                            "height": bottom - top,
                        })
            print(json.dumps({"ok": True, "lines": lines}, ensure_ascii=False), flush=True)
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc(file=sys.stderr)
        raise
