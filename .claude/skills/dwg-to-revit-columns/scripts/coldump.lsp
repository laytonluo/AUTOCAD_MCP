;;; coldump.lsp
;;; Export entities on given layers to CSV (next to the DWG).
;;; Usage in AutoCAD:
;;;   (load "D:/MCP/coldump.lsp")
;;;   COLDUMP                                  -> layers in *coldump-layers*
;;;   (coldump-run '("AXIS" "AXIS-TXT" "COL-L" "AN-COL" "A5-COL"))  -> returns CSV path
;;; Output: <dwg name>_col_layers.csv  (ANSI code page, e.g. Big5 on zh-TW Windows)
;;; Columns: layer,type,handle,closed,x,y,x2,y2,radius,text,vertices
;;;   META      text = "INSUNITS=n" (first data row; 4=mm 5=cm 6=m 1=inch 2=ft)
;;;   LINE      x,y = start   x2,y2 = end
;;;   ARC       x,y = center  x2,y2 = start/end angle (rad)  radius
;;;   CIRCLE    x,y = center  radius
;;;   LWPOLYLINE x,y = first vertex, closed = 0/1, vertices = "x y;x y;..." (bulges ignored)
;;;   TEXT/MTEXT x,y = insertion point, text = content ("," replaced by ";")
;;;   INSERT    x,y = insertion point, text = block name
;;;   ATTRIB    x,y = insertion point, text = attribute value (e.g. grid bubble name)

(vl-load-com)

(setq *coldump-layers* '("COL-L" "AN-COL" "A5-COL"))

(defun coldump:n (v) (rtos v 2 4))
(defun coldump:csv (s) (vl-string-translate "," ";" s))
(defun coldump:join (lst sep / s)
  (setq s nil)
  (foreach x lst (setq s (if s (strcat s sep x) x)))
  (if s s "")
)
(defun coldump:verts (ed / out)
  (foreach g ed
    (if (= (car g) 10)
      (setq out (cons (strcat (coldump:n (cadr g)) " " (coldump:n (caddr g))) out))
    )
  )
  (coldump:join (reverse out) ";")
)
(defun coldump:row (lay typ hnd closed p10 tail text verts)
  (strcat lay "," typ "," hnd "," closed ","
          (if p10 (coldump:n (car p10)) "") "," (if p10 (coldump:n (cadr p10)) "") ","
          tail "," text "," verts)
)

;; attributes of an INSERT (grid bubbles are often blocks with attributes)
(defun coldump:attribs (ename lay f / e ed n)
  (setq n 0 e (entnext ename))
  (while (and e (setq ed (entget e)) (= (cdr (assoc 0 ed)) "ATTRIB"))
    (write-line (coldump:row lay "ATTRIB" (cdr (assoc 5 ed)) "" (cdr (assoc 10 ed)) ",,"
                             (coldump:csv (cdr (assoc 1 ed))) "") f)
    (setq n (1+ n) e (entnext e))
  )
  n
)

(defun coldump-run (layers / path f lay ss i en ed typ p10 p11 cnt tail)
  (setq path (strcat (getvar "DWGPREFIX")
                     (vl-filename-base (getvar "DWGNAME"))
                     "_col_layers.csv")
        f    (open path "w"))
  (write-line "layer,type,handle,closed,x,y,x2,y2,radius,text,vertices" f)
  (write-line (strcat "*,META,,,,,,,," "INSUNITS=" (itoa (getvar "INSUNITS")) ",") f)
  (foreach lay layers
    (setq cnt 0)
    (if (setq ss (ssget "_X" (list (cons 8 lay))))
      (progn
        (setq i 0)
        (repeat (sslength ss)
          (setq en  (ssname ss i)
                ed  (entget en)
                typ (cdr (assoc 0 ed))
                p10 (cdr (assoc 10 ed))
                p11 (cdr (assoc 11 ed))
                i   (1+ i)
                cnt (1+ cnt))
          (setq tail
            (strcat
              (cond ((and (= typ "LINE") p11)
                     (strcat (coldump:n (car p11)) "," (coldump:n (cadr p11))))
                    ((= typ "ARC")
                     (strcat (coldump:n (cdr (assoc 50 ed))) "," (coldump:n (cdr (assoc 51 ed)))))
                    (T ","))
              "," (if (member typ '("CIRCLE" "ARC")) (coldump:n (cdr (assoc 40 ed))) "")))
          (write-line
            (coldump:row lay typ (cdr (assoc 5 ed))
              (cond ((= typ "LWPOLYLINE") (if (= 1 (logand 1 (cdr (assoc 70 ed)))) "1" "0"))
                    ((= typ "CIRCLE") "1")
                    (T ""))
              p10 tail
              (cond ((member typ '("TEXT" "MTEXT")) (coldump:csv (cdr (assoc 1 ed))))
                    ((= typ "INSERT") (coldump:csv (cdr (assoc 2 ed))))
                    (T ""))
              (if (= typ "LWPOLYLINE") (coldump:verts ed) ""))
            f)
          (if (and (= typ "INSERT") (= 1 (cdr (assoc 66 ed))))
            (setq cnt (+ cnt (coldump:attribs en lay f))))
        )
      )
    )
    (princ (strcat "\n" lay ": " (itoa cnt) " entities"))
  )
  (close f)
  (princ (strcat "\nSaved: " path))
  path
)

(defun c:COLDUMP () (coldump-run *coldump-layers*) (princ))

(princ "\ncoldump.lsp loaded. COLDUMP, or (coldump-run '(\"LAYER1\" \"LAYER2\")).")
(princ)
